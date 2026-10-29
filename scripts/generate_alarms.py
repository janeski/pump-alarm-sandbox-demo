"""Generate a month of pump alarms with a few patterns hidden in the noise.

Patterns for the AI to find:
  P-101  HIGH_TEMP at night, more often and longer every week, critical from mid-month; VIBRATION in the last week.
  P-204  LOW_PRESSURE every Monday morning (a weekly pattern).
  P-402  DRY_RUN, critical, starting 20 October.
  P-310  lots of COMM_LOSS info alarms: the most alarms, but harmless (a red herring).

Usage:  py -3.12 scripts/generate_alarms.py   (writes samples/pump_alarms_october.csv)
"""
import csv
import random
from datetime import datetime, timedelta
from pathlib import Path

random.seed(42)

PUMPS = {
    "P-101": "Bergen-North", "P-102": "Bergen-North", "P-103": "Bergen-North",
    "P-201": "Oslo-East", "P-202": "Oslo-East", "P-203": "Oslo-East", "P-204": "Oslo-East",
    "P-301": "Trondheim", "P-302": "Trondheim", "P-310": "Trondheim",
    "P-401": "Stavanger", "P-402": "Stavanger",
}

# Everyday background alarms: (code, severity, typical minutes)
BACKGROUND = [
    ("DOOR_OPEN", "info", 5),
    ("POWER_DIP", "info", 2),
    ("LOW_FLOW", "warning", 10),
    ("HIGH_TEMP", "warning", 8),
    ("LOW_PRESSURE", "warning", 6),
    ("VIBRATION", "warning", 7),
    ("MOTOR_OVERLOAD", "warning", 4),
    ("SEAL_LEAK", "warning", 15),
]

START = datetime(2026, 10, 1)
DAYS = 31
rows = []


def add(ts, pump, code, severity, minutes):
    rows.append([ts.strftime("%Y-%m-%dT%H:%M:%S"), pump, PUMPS[pump], code, severity, max(1, round(minutes))])


def at(day, hour_from, hour_to):
    return START + timedelta(days=day, hours=random.uniform(hour_from, hour_to))


for day in range(DAYS):
    date = START + timedelta(days=day)

    # Background noise for every pump.
    for pump in PUMPS:
        for _ in range(random.randint(1, 4)):
            code, severity, minutes = random.choice(BACKGROUND)
            add(at(day, 0, 24), pump, code, severity, random.expovariate(1 / minutes))

    # P-101: night-time overheating that gets worse.
    progress = day / (DAYS - 1)
    for _ in range(round(random.uniform(0, 1) + progress * 3)):
        severity = "critical" if day >= 14 and random.random() < 0.3 + progress * 0.6 else "warning"
        add(at(day, 0, 5), "P-101", "HIGH_TEMP", severity, random.gauss(10 + progress * 50, 5))
    if day >= 24 and random.random() < 0.7:
        add(at(day, 0, 6), "P-101", "VIBRATION", "critical", random.gauss(35, 10))

    # P-204: low pressure every Monday morning.
    if date.weekday() == 0:
        for _ in range(random.randint(3, 5)):
            add(at(day, 6, 9), "P-204", "LOW_PRESSURE", random.choice(["warning", "critical"]), random.gauss(20, 6))

    # P-402: dry running from 20 October.
    if date.day >= 20:
        for _ in range(random.randint(1, 3)):
            add(at(day, 0, 24), "P-402", "DRY_RUN", "critical", random.gauss(6, 2))

    # P-310: noisy, harmless communication loss.
    for _ in range(random.randint(10, 20)):
        add(at(day, 0, 24), "P-310", "COMM_LOSS", "info", random.expovariate(1 / 30))

rows.sort(key=lambda r: r[0])

out = Path(__file__).resolve().parent.parent / "samples" / "pump_alarms_october.csv"
with out.open("w", newline="") as f:
    writer = csv.writer(f)
    writer.writerow(["timestamp", "pump_id", "site", "alarm_code", "severity", "duration_min"])
    writer.writerows(rows)

print(f"{len(rows)} alarms -> {out}")
