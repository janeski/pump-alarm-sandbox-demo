"""More synthetic pump alarm data for the demo, one file per scenario, same columns as pump_alarms_october.csv.

  samples/pump_alarms_q3.csv
      A quarter (July-September 2026), 16 pumps on 5 sites. Stories to find:
        - Heatwave: Oslo-East pumps get many daytime HIGH_TEMP alarms from 14 July to 4 August.
        - Site outage: on 14 August 02:10-04:30 every Trondheim pump has POWER_DIP + COMM_LOSS at the
          same time. The cause is the site, not the pumps.
        - Maintenance that worked: P-205 has daily SEAL_LEAK warnings until 20 August, then none.
        - Wearing bearing: P-503 VIBRATION gets more frequent and longer through September, critical at the end.
        - Flapping sensor: P-402 has hundreds of 1-minute LOW_FLOW info alarms. Noise, not a pump problem.

  samples/pump_alarms_messy.csv
      Two weeks of data with typical quality problems, for "clean the data first" questions:
        duplicate rows, two timestamp formats, pump ids in lower case or with spaces, missing severity,
        a misspelled alarm code (HIGH_TMP), zero and negative durations. Underneath: P-101 overheats and
        P-304 dry-runs.

  samples/pump_alarms_storm.csv
      One week in Bergen (2-8 November 2026) with a storm on 4 November 18:00 - 5 November 06:00:
      power dips and high water levels on every Bergen pump during the storm, and P-104 that keeps
      alarming after the storm (DRY_RUN): the one pump that was damaged.

Usage:  py -3.12 scripts/generate_more_alarms.py
"""
import csv
import random
from datetime import datetime, timedelta
from pathlib import Path

SAMPLES = Path(__file__).resolve().parent.parent / "samples"
HEADER = ["timestamp", "pump_id", "site", "alarm_code", "severity", "duration_min"]

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


def ts(t):
    return t.strftime("%Y-%m-%dT%H:%M:%S")


def write(name, rows):
    rows.sort(key=lambda r: r[0])
    path = SAMPLES / name
    with path.open("w", newline="") as f:
        w = csv.writer(f)
        w.writerow(HEADER)
        w.writerows(rows)
    print(f"{len(rows):6} alarms -> {path.name}")


def background(rng, pumps, start, days, per_day=(1, 3)):
    rows = []
    for day in range(days):
        for pump, site in pumps.items():
            for _ in range(rng.randint(*per_day)):
                code, sev, minutes = rng.choice(BACKGROUND)
                t = start + timedelta(days=day, hours=rng.uniform(0, 24))
                rows.append([ts(t), pump, site, code, sev, max(1, round(rng.expovariate(1 / minutes)))])
    return rows


# ---------------------------------------------------------------- Q3
def quarter():
    rng = random.Random(2026_07)
    pumps = {
        "P-101": "Bergen-North", "P-102": "Bergen-North", "P-103": "Bergen-North",
        "P-201": "Oslo-East", "P-202": "Oslo-East", "P-203": "Oslo-East", "P-204": "Oslo-East", "P-205": "Oslo-East",
        "P-301": "Trondheim", "P-302": "Trondheim", "P-303": "Trondheim",
        "P-401": "Stavanger", "P-402": "Stavanger",
        "P-501": "Tromso", "P-502": "Tromso", "P-503": "Tromso",
    }
    start, days = datetime(2026, 7, 1), 92
    rows = background(rng, pumps, start, days)

    def add(t, pump, code, sev, minutes):
        rows.append([ts(t), pump, pumps[pump], code, sev, max(1, round(minutes))])

    for day in range(days):
        date = start + timedelta(days=day)

        # Heatwave in Oslo, 14 July - 4 August, daytime.
        if datetime(2026, 7, 14) <= date <= datetime(2026, 8, 4):
            for pump in [p for p, s in pumps.items() if s == "Oslo-East"]:
                for _ in range(rng.randint(2, 5)):
                    sev = "critical" if rng.random() < 0.35 else "warning"
                    add(date + timedelta(hours=rng.uniform(11, 18)), pump, "HIGH_TEMP", sev, rng.gauss(25, 8))

        # P-205 seal leak until the maintenance on 20 August.
        if date < datetime(2026, 8, 20):
            for _ in range(rng.randint(1, 3)):
                add(date + timedelta(hours=rng.uniform(0, 24)), "P-205", "SEAL_LEAK", "warning", rng.gauss(30, 10))

        # P-503 bearing wear through September.
        if date >= datetime(2026, 9, 1):
            progress = (date - datetime(2026, 9, 1)).days / 29
            for _ in range(round(rng.uniform(0, 1) + progress * 5)):
                sev = "critical" if progress > 0.7 and rng.random() < 0.6 else "warning"
                add(date + timedelta(hours=rng.uniform(0, 24)), "P-503", "VIBRATION", sev, rng.gauss(10 + progress * 40, 5))

        # P-402 flapping flow sensor: many 1-minute info alarms.
        for _ in range(rng.randint(5, 12)):
            add(date + timedelta(hours=rng.uniform(0, 24)), "P-402", "LOW_FLOW", "info", 1)

    # Trondheim site outage, 14 August 02:10-04:30.
    outage = datetime(2026, 8, 14, 2, 10)
    for pump in [p for p, s in pumps.items() if s == "Trondheim"]:
        add(outage + timedelta(minutes=rng.uniform(0, 3)), pump, "POWER_DIP", "critical", rng.gauss(138, 4))
        for _ in range(rng.randint(3, 6)):
            add(outage + timedelta(minutes=rng.uniform(5, 135)), pump, "COMM_LOSS", "critical", rng.gauss(20, 5))

    write("pump_alarms_q3.csv", rows)


# ---------------------------------------------------------------- messy
def messy():
    rng = random.Random(1313)
    pumps = {
        "P-101": "Bergen-North", "P-102": "Bergen-North",
        "P-201": "Oslo-East", "P-202": "Oslo-East",
        "P-301": "Trondheim", "P-304": "Trondheim",
        "P-401": "Stavanger",
    }
    start, days = datetime(2026, 10, 12), 14
    rows = background(rng, pumps, start, days)

    for day in range(days):
        date = start + timedelta(days=day)
        for _ in range(rng.randint(2, 4)):  # P-101 overheating
            t = date + timedelta(hours=rng.uniform(0, 6))
            rows.append([ts(t), "P-101", "Bergen-North", "HIGH_TEMP", "critical" if day > 6 else "warning",
                         round(rng.gauss(30 + day * 2, 5))])
        if day >= 5:  # P-304 dry running from day 5
            for _ in range(rng.randint(1, 3)):
                t = date + timedelta(hours=rng.uniform(0, 24))
                rows.append([ts(t), "P-304", "Trondheim", "DRY_RUN", "critical", round(rng.gauss(6, 2))])

    # Now make it messy.
    out = []
    for r in rows:
        r = list(r)
        x = rng.random()
        if x < 0.15:  # Norwegian-style timestamps
            r[0] = datetime.fromisoformat(r[0]).strftime("%d.%m.%Y %H:%M")
        if rng.random() < 0.08:  # pump id variants
            r[1] = rng.choice([r[1].lower(), f" {r[1]}", r[1].replace("-", "")])
        if rng.random() < 0.04:  # missing severity
            r[4] = ""
        if r[3] == "HIGH_TEMP" and rng.random() < 0.10:  # typo in the code
            r[3] = "HIGH_TMP"
        if rng.random() < 0.02:  # bad durations
            r[5] = rng.choice([0, -1, -5])
        out.append(r)
        if rng.random() < 0.03:  # duplicate rows
            out.append(list(r))

    out.sort(key=lambda r: str(r[0]))
    path = SAMPLES / "pump_alarms_messy.csv"
    with path.open("w", newline="") as f:
        w = csv.writer(f)
        w.writerow(HEADER)
        w.writerows(out)
    print(f"{len(out):6} alarms -> {path.name}")


# ---------------------------------------------------------------- storm
def storm():
    rng = random.Random(4114)
    pumps = {f"P-10{i}": "Bergen-North" for i in range(1, 7)} | {f"P-11{i}": "Bergen-South" for i in range(1, 5)}
    start, days = datetime(2026, 11, 2), 7
    rows = background(rng, pumps, start, days, per_day=(0, 2))

    storm_start, storm_end = datetime(2026, 11, 4, 18), datetime(2026, 11, 5, 6)
    hours = (storm_end - storm_start).total_seconds() / 3600
    for pump, site in pumps.items():
        for _ in range(rng.randint(3, 7)):
            t = storm_start + timedelta(hours=rng.uniform(0, hours))
            rows.append([ts(t), pump, site, "POWER_DIP", "critical", max(1, round(rng.gauss(8, 3)))])
        for _ in range(rng.randint(2, 5)):
            t = storm_start + timedelta(hours=rng.uniform(1, hours))
            rows.append([ts(t), pump, site, "HIGH_LEVEL", "critical", max(1, round(rng.gauss(45, 15)))])

    # P-104 was damaged: dry running after the storm, getting worse.
    t = storm_end
    while t < start + timedelta(days=days):
        t += timedelta(hours=rng.uniform(1, 4))
        hours_after = (t - storm_end).total_seconds() / 3600
        rows.append([ts(t), "P-104", "Bergen-North", "DRY_RUN", "critical", max(1, round(rng.gauss(5 + hours_after / 10, 2)))])

    write("pump_alarms_storm.csv", rows)


quarter()
messy()
storm()
