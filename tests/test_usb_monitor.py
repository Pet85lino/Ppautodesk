"""
tests/test_usb_monitor.py
-------------------------
Pruebas del monitoreo energetico: dataclasses, formato y alertas.
"""

from __future__ import annotations

import unittest

from usb.usb_monitor import PowerStatus, SerialPortInfo, check_alerts


class PowerStatusTest(unittest.TestCase):
    def test_format_desktop(self) -> None:
        status = PowerStatus(available=False)
        self.assertIn("Sin sensor", status.format())

    def test_format_charging(self) -> None:
        status = PowerStatus(available=True, percent=85.0, plugged=True, secs_left=None)
        text = status.format()
        self.assertIn("85%", text)
        self.assertIn("Cargando", text)

    def test_format_discharging_with_time(self) -> None:
        status = PowerStatus(available=True, percent=40.0, plugged=False, secs_left=3600)
        text = status.format()
        self.assertIn("Descargando", text)
        self.assertIn("60 min", text)


class SerialPortInfoTest(unittest.TestCase):
    def test_format_with_meter(self) -> None:
        port = SerialPortInfo("COM3", "USB Serial", meter_model="RDTech UM25C")
        self.assertIn("[RDTech UM25C]", port.format())

    def test_format_without_meter(self) -> None:
        port = SerialPortInfo("COM4", "USB Serial")
        self.assertNotIn("[", port.format())


class AlertsTest(unittest.TestCase):
    def test_critical_battery(self) -> None:
        current = PowerStatus(available=True, percent=5.0, plugged=False)
        alerts = check_alerts(None, current, 60.0)
        self.assertTrue(any("critica" in a for a in alerts))

    def test_abnormal_discharge(self) -> None:
        previous = PowerStatus(available=True, percent=90.0, plugged=False)
        current = PowerStatus(available=True, percent=85.0, plugged=False)
        # 5 % en 60 s = 5 %/min -> muy por encima del umbral de 1.5 %/min.
        alerts = check_alerts(previous, current, 60.0)
        self.assertTrue(any("Descarga anormal" in a for a in alerts))

    def test_normal_discharge_no_alert(self) -> None:
        previous = PowerStatus(available=True, percent=80.0, plugged=False)
        current = PowerStatus(available=True, percent=79.9, plugged=False)
        alerts = check_alerts(previous, current, 300.0)
        self.assertEqual(alerts, [])

    def test_plug_transition(self) -> None:
        previous = PowerStatus(available=True, percent=50.0, plugged=False)
        current = PowerStatus(available=True, percent=50.0, plugged=True)
        alerts = check_alerts(previous, current, 5.0)
        self.assertTrue(any("conectada" in a for a in alerts))

    def test_desktop_returns_empty(self) -> None:
        self.assertEqual(check_alerts(None, PowerStatus(available=False), 5.0), [])


if __name__ == "__main__":
    unittest.main()
