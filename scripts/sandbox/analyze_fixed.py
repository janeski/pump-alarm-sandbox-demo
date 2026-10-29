# Step 2: fixed analysis, runs inside the ACA sandbox. The AI replaces this in step 4.
import pandas as pd

df = pd.read_csv("/root/pump_alarms.csv", parse_dates=["timestamp"])
print(df.shape)
print(df.groupby("pump_id").agg(
    alarms=("alarm_code", "size"),
    critical=("severity", lambda s: (s == "critical").sum()),
    minutes=("duration_min", "sum"),
).sort_values("critical", ascending=False))
