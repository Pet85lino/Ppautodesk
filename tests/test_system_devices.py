"""
tests/test_system_devices.py
----------------------------
Pruebas del puente con los dispositivos Bluetooth del SO: parser de
Get-PnpDevice y fusion con el escaneo BLE.
"""

from __future__ import annotations

import json
import unittest
from dataclasses import dataclass, field

from ble.system_devices import (
    STATUS_CONNECTED,
    STATUS_NEARBY,
    STATUS_PAIRED,
    SystemDevice,
    merge_with_scan,
    parse_pnp_devices,
)


@dataclass
class FakeDevice:
    name: str
    mac: str
    rssi: int
    manufacturer: str = "Test"
    uuids: list = field(default_factory=list)


class ParsePnpTest(unittest.TestCase):
    def test_parses_and_deduplicates_instances(self) -> None:
        # Un mismo fisico aparece como raiz + servicios hijos.
        payload = json.dumps([
            {
                "FriendlyName": "MAXELL DYNAMIC+",
                "Status": "OK",
                "InstanceId": "BTHENUM\\DEV_AABBCCDDEEFF\\8&1234",
            },
            {
                "FriendlyName": "MAXELL DYNAMIC+ Avrcp Transport",
                "Status": "Unknown",
                "InstanceId": "BTHENUM\\{0000110e}_VID&0001\\8&AABBCCDDEEFF_C00000000",
            },
            {
                "FriendlyName": "TG-345",
                "Status": "Unknown",
                "InstanceId": "BTHENUM\\DEV_112233445566\\8&5678",
            },
        ])
        devices = parse_pnp_devices(payload)
        self.assertEqual(len(devices), 2)

        by_mac = {d.mac: d for d in devices}
        maxell = by_mac["AA:BB:CC:DD:EE:FF"]
        # Conectado si CUALQUIER instancia esta OK; nombre de la raiz.
        self.assertTrue(maxell.connected)
        self.assertEqual(maxell.name, "MAXELL DYNAMIC+")
        self.assertFalse(by_mac["11:22:33:44:55:66"].connected)

    def test_single_object_not_array(self) -> None:
        # ConvertTo-Json devuelve un objeto suelto con 1 solo resultado.
        payload = json.dumps({
            "FriendlyName": "Meetion_MS_BT1",
            "Status": "OK",
            "InstanceId": "BTHLE\\DEV_010203040506\\9&abc",
        })
        devices = parse_pnp_devices(payload)
        self.assertEqual(len(devices), 1)
        self.assertEqual(devices[0].mac, "01:02:03:04:05:06")
        self.assertTrue(devices[0].connected)

    def test_entries_without_mac_are_ignored(self) -> None:
        payload = json.dumps([
            {"FriendlyName": "Radio Bluetooth Intel", "Status": "OK",
             "InstanceId": "USB\\VID_8087&PID_0026\\5&123"},
        ])
        self.assertEqual(parse_pnp_devices(payload), [])

    def test_invalid_json_returns_empty(self) -> None:
        self.assertEqual(parse_pnp_devices("no es json"), [])

    def test_isconnected_overrides_status_ok(self) -> None:
        # CASO REAL reportado: emparejado sin conectar tambien tiene
        # Status OK; la propiedad IsConnected=False debe mandar.
        payload = json.dumps([
            {
                "FriendlyName": "A12 pro",
                "Status": "OK",
                "InstanceId": "BTHENUM\\DEV_AAAAAAAAAAAA\\8&1",
                "IsConnected": False,
                "Battery": None,
            },
            {
                "FriendlyName": "MAXELL DYNAMIC+",
                "Status": "OK",
                "InstanceId": "BTHENUM\\DEV_BBBBBBBBBBBB\\8&2",
                "IsConnected": True,
                "Battery": 50,
            },
        ])
        by_mac = {d.mac: d for d in parse_pnp_devices(payload)}
        self.assertFalse(by_mac["AA:AA:AA:AA:AA:AA"].connected)  # emparejado
        self.assertTrue(by_mac["BB:BB:BB:BB:BB:BB"].connected)   # conectado
        self.assertEqual(by_mac["BB:BB:BB:BB:BB:BB"].battery, 50)
        self.assertIsNone(by_mac["AA:AA:AA:AA:AA:AA"].battery)

    def test_isconnected_as_string(self) -> None:
        # ConvertTo-Json puede serializar el bool como cadena.
        payload = json.dumps({
            "FriendlyName": "TWS",
            "Status": "Unknown",
            "InstanceId": "BTHENUM\\DEV_CCCCCCCCCCCC\\8&3",
            "IsConnected": "True",
            "Battery": "77",
        })
        device = parse_pnp_devices(payload)[0]
        self.assertTrue(device.connected)
        self.assertEqual(device.battery, 77)

    def test_battery_clamped_and_invalid_ignored(self) -> None:
        payload = json.dumps([
            {"FriendlyName": "X", "Status": "OK",
             "InstanceId": "BTHENUM\\DEV_DDDDDDDDDDDD\\1",
             "IsConnected": True, "Battery": 150},
            {"FriendlyName": "Y", "Status": "OK",
             "InstanceId": "BTHENUM\\DEV_EEEEEEEEEEEE\\1",
             "IsConnected": True, "Battery": "no-numero"},
        ])
        by_mac = {d.mac: d for d in parse_pnp_devices(payload)}
        self.assertEqual(by_mac["DD:DD:DD:DD:DD:DD"].battery, 100)  # clamp
        self.assertIsNone(by_mac["EE:EE:EE:EE:EE:EE"].battery)


class MergeTest(unittest.TestCase):
    def test_order_connected_paired_nearby(self) -> None:
        system = [
            SystemDevice("TG-345", "11:22:33:44:55:66", connected=False),
            SystemDevice("MAXELL DYNAMIC+", "AA:BB:CC:DD:EE:FF", connected=True),
        ]
        scanned = [FakeDevice("Samsung TV", "BC:45:5B:CF:7B:9B", -87)]

        rows = merge_with_scan(system, scanned)
        self.assertEqual(
            [r["status"] for r in rows],
            [STATUS_CONNECTED, STATUS_PAIRED, STATUS_NEARBY],
        )
        self.assertEqual(rows[0]["name"], "MAXELL DYNAMIC+")
        self.assertIsNone(rows[0]["rssi"])  # no anuncia BLE
        self.assertEqual(rows[2]["rssi"], -87)

    def test_system_device_enriched_with_ble_scan(self) -> None:
        # El mismo fisico esta emparejado Y anunciando BLE: una sola fila
        # con el RSSI/fabricante del advertisement.
        system = [SystemDevice("MAXELL DYNAMIC+", "AA:BB:CC:DD:EE:FF", connected=True)]
        scanned = [FakeDevice("MAXELL", "aa:bb:cc:dd:ee:ff", -60, manufacturer="ID 0x1234")]

        rows = merge_with_scan(system, scanned)
        self.assertEqual(len(rows), 1)
        self.assertEqual(rows[0]["status"], STATUS_CONNECTED)
        self.assertEqual(rows[0]["rssi"], -60)
        self.assertEqual(rows[0]["manufacturer"], "ID 0x1234")
        self.assertIsNotNone(rows[0]["device"])

    def test_nearby_sorted_by_rssi(self) -> None:
        scanned = [
            FakeDevice("Lejano", "AA:00:00:00:00:01", -95),
            FakeDevice("Cercano", "AA:00:00:00:00:02", -50),
        ]
        rows = merge_with_scan([], scanned)
        self.assertEqual([r["name"] for r in rows], ["Cercano", "Lejano"])

    def test_system_battery_propagated_to_row(self) -> None:
        system = [SystemDevice("MAXELL", "AA:BB:CC:DD:EE:FF",
                               connected=True, battery=50)]
        rows = merge_with_scan(system, [])
        self.assertEqual(rows[0]["battery"], 50)


class UsbDiffTest(unittest.TestCase):
    def test_added_and_removed(self) -> None:
        from usb.usb_monitor import diff_usb_devices

        added, removed = diff_usb_devices(
            ["Mouse USB", "Medidor UM25C"],
            ["Mouse USB", "Case TWS"],
        )
        self.assertEqual(added, ["Case TWS"])
        self.assertEqual(removed, ["Medidor UM25C"])

    def test_no_changes(self) -> None:
        from usb.usb_monitor import diff_usb_devices

        self.assertEqual(diff_usb_devices(["A"], ["A"]), ([], []))


if __name__ == "__main__":
    unittest.main()
