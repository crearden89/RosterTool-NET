// Port of src/lib/stage2/g999.fortnight.fixture.ts.
// ─── G999 fortnight 31/08-13/09/2026 - REAL live data, frozen 01/09/2026 ─────
// The second real-data fixture, and the one that exists because the first was
// not enough. G999Fixture was frozen from a benchmark upload carrying
// employee_count 0, so every per-employee admin task in the catalogue resolved
// to nothing and the fixture never exercised them. When a real upload arrived
// with 20 employees the admin load doubled on some days, the morning shape
// changed, and the plan came out with thirty-three blank tiles across the
// fortnight - none of which any test could see.
//
// So this one is a whole fortnight of the CURRENT benchmark, and it asserts
// the thing that actually matters to a store: no holes in anybody's shift.
// (The production harness that plans and rosters it end to end reads these:
// the benchmark, the trade curve, the labour-group windows and the admin and
// reset catalogues. The catalogues are the TypeScript's JSON, verbatim.)

using System.Text.Json;
using Roster.Engine.Core;
using Roster.Engine.Planning;

namespace Roster.Engine.Tests.Rostering.Fortnight;

public static class G999FortnightFixture
{
    private static List<T> Parse<T>(string json) => JsonSerializer.Deserialize<List<T>>(json, RosterJson.Options)!;

    // Per date, the benchmark's six group hours (90% OE), in the TypeScript's order.
    public static readonly IReadOnlyDictionary<string, double[]> Bench = new Dictionary<string, double[]>
    {
        ["2026-08-31"] = [11.9553, 4.0, 1.440302, 6.804774, 9.515, 11.5],
        ["2026-09-01"] = [12.039111, 2.666667, 8.756747, 6.858321, 6.841667, 11.5],
        ["2026-09-02"] = [12.9515, 2.166667, 1.617191, 7.441237, 5.458333, 11.5],
        ["2026-09-03"] = [13.684978, 3.3, 1.550969, 7.90985, 4.958333, 11.5],
        ["2026-09-04"] = [14.693711, 2.166667, 6.651547, 8.554314, 5.025, 11.5],
        ["2026-09-05"] = [14.724667, 2.166667, 1.725191, 9.828416, 4.791667, 10.990556],
        ["2026-09-06"] = [12.310622, 0.333333, 1.545636, 8.604807, 4.791667, 10.5],
        ["2026-09-07"] = [11.780389, 4.0, 1.440302, 6.693028, 9.075, 11.5],
        ["2026-09-08"] = [11.876589, 2.666667, 7.312302, 6.754489, 11.291667, 11.5],
        ["2026-09-09"] = [12.720289, 2.166667, 1.617191, 7.293519, 5.458333, 11.5],
        ["2026-09-10"] = [13.174411, 3.3, 1.550969, 7.583652, 4.775, 11.5],
        ["2026-09-11"] = [13.962911, 2.166667, 5.994347, 8.087417, 5.025, 11.5],
        ["2026-09-12"] = [14.376578, 2.166667, 1.622524, 9.576371, 4.791667, 10.822],
        ["2026-09-13"] = [14.1734, 0.333333, 1.535413, 10.032938, 4.791667, 10.5],
    };

    /// <summary>Quarter-hour trade share by weekday (0 = Monday), 06:00 → 21:45.</summary>
    public static readonly IReadOnlyDictionary<int, double[]> Curve = new Dictionary<int, double[]>
    {
        [0] = [0, 0, 0, 0, 0, 0, 0, 0, 0, 6.2e-05, 0.003283, 0.006855, 0.006277, 0.011212, 0.010551, 0.009519, 0.01976, 0.026367, 0.021494, 0.025954, 0.028411, 0.023992, 0.025149, 0.025624, 0.027069, 0.025892, 0.02998, 0.025768, 0.024798, 0.024694, 0.022795, 0.026202, 0.028411, 0.027688, 0.028328, 0.026202, 0.023497, 0.027668, 0.028122, 0.023518, 0.026367, 0.037104, 0.027915, 0.032726, 0.0338, 0.028824, 0.020049, 0.023311, 0.026759, 0.020482, 0.014226, 0.009746, 0.012636, 0.00702, 0.007929, 0.005905, 6.2e-05, 0, 0, 0, 0, 0, 0, 0],
        [1] = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0.001997, 0.008656, 0.014315, 0.012793, 0.010344, 0.018334, 0.015956, 0.019142, 0.025111, 0.023541, 0.027179, 0.026942, 0.024706, 0.030508, 0.032839, 0.021877, 0.028321, 0.026466, 0.021996, 0.031983, 0.022566, 0.019285, 0.021211, 0.030461, 0.029272, 0.030675, 0.022019, 0.025467, 0.0263, 0.029771, 0.024825, 0.023137, 0.02492, 0.025134, 0.030366, 0.031888, 0.024754, 0.031959, 0.025325, 0.016455, 0.009654, 0.015052, 0.010534, 0.00925, 0.009654, 0.006848, 0.000214, 0, 0, 0, 0, 0, 0, 0],
        [2] = [0, 0, 0, 0, 0, 0, 0, 0, 2e-05, 2e-05, 0.004663, 0.011101, 0.012413, 0.015441, 0.018368, 0.018388, 0.019861, 0.016712, 0.025674, 0.023151, 0.017237, 0.039399, 0.034979, 0.030397, 0.022525, 0.024059, 0.031346, 0.025593, 0.022727, 0.017459, 0.029408, 0.026683, 0.02737, 0.029408, 0.025957, 0.032052, 0.024281, 0.020628, 0.022768, 0.02198, 0.029812, 0.033889, 0.02628, 0.031588, 0.025593, 0.026845, 0.018569, 0.018408, 0.023111, 0.01867, 0.011, 0.010698, 0.012191, 0.00878, 0.007125, 0.004703, 0.000666, 0, 0, 0, 0, 0, 0, 0],
        [3] = [0, 0, 0, 0, 0, 0, 0, 0, 0, 2.1e-05, 0.002168, 0.009904, 0.025396, 0.009549, 0.010863, 0.01299, 0.018828, 0.020121, 0.02477, 0.024103, 0.029211, 0.025667, 0.026709, 0.026793, 0.025833, 0.023999, 0.029024, 0.025291, 0.02525, 0.023331, 0.031234, 0.035737, 0.026, 0.030441, 0.033048, 0.030462, 0.023457, 0.023123, 0.023728, 0.029295, 0.023561, 0.028669, 0.023019, 0.027585, 0.021789, 0.024228, 0.023519, 0.024437, 0.018724, 0.016034, 0.01618, 0.012698, 0.009695, 0.010738, 0.006755, 0.005817, 0.000209, 0, 0, 0, 0, 0, 0, 0],
        [4] = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0.002235, 0.007374, 0.012179, 0.017151, 0.011713, 0.016536, 0.021583, 0.018622, 0.0227, 0.019665, 0.030019, 0.026164, 0.032346, 0.023203, 0.033184, 0.030186, 0.036574, 0.030875, 0.028585, 0.03095, 0.028529, 0.023352, 0.030354, 0.026089, 0.022291, 0.03013, 0.026034, 0.032439, 0.027151, 0.020372, 0.023389, 0.020168, 0.022831, 0.027914, 0.026834, 0.020633, 0.026741, 0.01743, 0.021006, 0.017486, 0.01203, 0.009963, 0.012998, 0.00648, 0.007188, 0.007393, 0.000931, 0, 0, 0, 0, 0, 0, 0],
        [5] = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0.005222, 0.011814, 0.017052, 0.016295, 0.014603, 0.015054, 0.023209, 0.028673, 0.029141, 0.0269, 0.032284, 0.030269, 0.025949, 0.03014, 0.032493, 0.026594, 0.032493, 0.035362, 0.0323, 0.022581, 0.025482, 0.03641, 0.034169, 0.02748, 0.024386, 0.035878, 0.022984, 0.02524, 0.022468, 0.024112, 0.02379, 0.022065, 0.021356, 0.027319, 0.020131, 0.018535, 0.021968, 0.020163, 0.013909, 0.011073, 0.007156, 0.006189, 0.005673, 0.005528, 0.005013, 0.003095, 0, 0, 0, 0, 0, 0, 0, 0],
        [6] = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0.001294, 0.009477, 0.012196, 0.010388, 0.016235, 0.017595, 0.024432, 0.025158, 0.018136, 0.022056, 0.025804, 0.026926, 0.031573, 0.022861, 0.030899, 0.02727, 0.030266, 0.026161, 0.036311, 0.034384, 0.032602, 0.03309, 0.028801, 0.029249, 0.026029, 0.0302, 0.024247, 0.028695, 0.026953, 0.029249, 0.033434, 0.028801, 0.023297, 0.024986, 0.021858, 0.021488, 0.024471, 0.01807, 0.014295, 0.015338, 0.005808, 0.006969, 0.007194, 0.004646, 0.005913, 0.004871, 2.6e-05, 0, 0, 0, 0, 0, 0, 0],
    };

    /// <summary>labour_group_rules for every weekday, verbatim.</summary>
    public static IReadOnlyList<WindowRule> Windows => windows.Value;

    private static readonly Lazy<List<WindowRule>> windows = new(() => Parse<WindowRule>(
        """
        [
          {
            "group": "admin",
            "start": 420,
            "end": 1020
          },
          {
            "group": "load_running",
            "start": 360,
            "end": 600
          },
          {
            "group": "reset",
            "start": 600,
            "end": 1200
          },
          {
            "group": "specials",
            "start": 1020,
            "end": 1260
          },
          {
            "group": "specials",
            "start": 1080,
            "end": 1260
          },
          {
            "group": "cleaning",
            "start": 1080,
            "end": 1260
          },
          {
            "group": "cleaning",
            "start": 540,
            "end": 600,
            "hours": 1
          }
        ]
        """));

    /// <summary>admin_tasks catalogue, verbatim.</summary>
    public static IReadOnlyList<AdminTaskRule> AdminTasks => adminTasks.Value;

    private static readonly Lazy<List<AdminTaskRule>> adminTasks = new(() => Parse<AdminTaskRule>(
        """
        [
          {
            "id": "a1",
            "name": "APM",
            "scheduleType": "weekly",
            "dayOfWeek": 0,
            "nthWeek": null,
            "regionDays": null,
            "durationType": "fixed",
            "fixedMinutes": 15,
            "minutesPerUnit": null,
            "minMinutes": null,
            "windowStartKind": "clock",
            "windowStartTime": 540,
            "windowStartOffsetMin": 0,
            "windowEndKind": "clock",
            "windowEndTime": 720,
            "windowEndOffsetMin": 0,
            "sortOrder": 10
          },
          {
            "id": "a2",
            "name": "End of Day",
            "scheduleType": "daily",
            "dayOfWeek": null,
            "nthWeek": null,
            "regionDays": null,
            "durationType": "fixed",
            "fixedMinutes": 17,
            "minutesPerUnit": null,
            "minMinutes": null,
            "windowStartKind": "close",
            "windowStartTime": null,
            "windowStartOffsetMin": 0,
            "windowEndKind": "none",
            "windowEndTime": null,
            "windowEndOffsetMin": 0,
            "sortOrder": 20
          },
          {
            "id": "a3",
            "name": "Comms Overhaul",
            "scheduleType": "weekly",
            "dayOfWeek": 0,
            "nthWeek": null,
            "regionDays": null,
            "durationType": "fixed",
            "fixedMinutes": 30,
            "minutesPerUnit": null,
            "minMinutes": null,
            "windowStartKind": "clock",
            "windowStartTime": 720,
            "windowStartOffsetMin": 0,
            "windowEndKind": "clock",
            "windowEndTime": 900,
            "windowEndOffsetMin": 0,
            "sortOrder": 30
          },
          {
            "id": "a4",
            "name": "Fresh Ordering",
            "scheduleType": "daily",
            "dayOfWeek": null,
            "nthWeek": null,
            "regionDays": null,
            "durationType": "fixed",
            "fixedMinutes": 150,
            "minutesPerUnit": null,
            "minMinutes": null,
            "windowStartKind": "close",
            "windowStartTime": null,
            "windowStartOffsetMin": -210,
            "windowEndKind": "close",
            "windowEndTime": null,
            "windowEndOffsetMin": 0,
            "sortOrder": 40
          },
          {
            "id": "a5",
            "name": "Longlife Date Checks",
            "scheduleType": "weekly",
            "dayOfWeek": 0,
            "nthWeek": null,
            "regionDays": null,
            "durationType": "fixed",
            "fixedMinutes": 120,
            "minutesPerUnit": null,
            "minMinutes": null,
            "windowStartKind": "none",
            "windowStartTime": null,
            "windowStartOffsetMin": 0,
            "windowEndKind": "clock",
            "windowEndTime": 510,
            "windowEndOffsetMin": 0,
            "sortOrder": 50
          },
          {
            "id": "a6",
            "name": "Non BAU STM Tasks",
            "scheduleType": "weekly",
            "dayOfWeek": 0,
            "nthWeek": null,
            "regionDays": null,
            "durationType": "fixed",
            "fixedMinutes": 60,
            "minutesPerUnit": null,
            "minMinutes": null,
            "windowStartKind": "none",
            "windowStartTime": null,
            "windowStartOffsetMin": 0,
            "windowEndKind": "none",
            "windowEndTime": null,
            "windowEndOffsetMin": 0,
            "sortOrder": 60
          },
          {
            "id": "a7",
            "name": "Parameter Deviation & Custom Bays",
            "scheduleType": "weekly",
            "dayOfWeek": 0,
            "nthWeek": null,
            "regionDays": null,
            "durationType": "fixed",
            "fixedMinutes": 15,
            "minutesPerUnit": null,
            "minMinutes": null,
            "windowStartKind": "none",
            "windowStartTime": null,
            "windowStartOffsetMin": 0,
            "windowEndKind": "clock",
            "windowEndTime": 780,
            "windowEndOffsetMin": 0,
            "sortOrder": 70
          },
          {
            "id": "a8",
            "name": "Safe Handover",
            "scheduleType": "weekly",
            "dayOfWeek": 4,
            "nthWeek": null,
            "regionDays": null,
            "durationType": "fixed",
            "fixedMinutes": 14,
            "minutesPerUnit": null,
            "minMinutes": null,
            "windowStartKind": "clock",
            "windowStartTime": 540,
            "windowStartOffsetMin": 0,
            "windowEndKind": "none",
            "windowEndTime": null,
            "windowEndOffsetMin": 0,
            "sortOrder": 90
          },
          {
            "id": "a9",
            "name": "Till Calibration",
            "scheduleType": "weekly",
            "dayOfWeek": null,
            "nthWeek": null,
            "regionDays": {
              "501": 0,
              "502": 3,
              "503": 3,
              "504": 1,
              "505": 0,
              "506": 0,
              "507": 1,
              "509": 1
            },
            "durationType": "till_calibration",
            "fixedMinutes": null,
            "minutesPerUnit": null,
            "minMinutes": null,
            "windowStartKind": "none",
            "windowStartTime": null,
            "windowStartOffsetMin": 0,
            "windowEndKind": "none",
            "windowEndTime": null,
            "windowEndOffsetMin": 0,
            "sortOrder": 100
          },
          {
            "id": "a10",
            "name": "UKG End of Day",
            "scheduleType": "daily",
            "dayOfWeek": null,
            "nthWeek": null,
            "regionDays": null,
            "durationType": "fixed",
            "fixedMinutes": 6,
            "minutesPerUnit": null,
            "minMinutes": null,
            "windowStartKind": "close",
            "windowStartTime": null,
            "windowStartOffsetMin": 0,
            "windowEndKind": "none",
            "windowEndTime": null,
            "windowEndOffsetMin": 0,
            "sortOrder": 110
          },
          {
            "id": "a11",
            "name": "Use By Date Check",
            "scheduleType": "daily",
            "dayOfWeek": null,
            "nthWeek": null,
            "regionDays": null,
            "durationType": "fixed",
            "fixedMinutes": 18,
            "minutesPerUnit": null,
            "minMinutes": null,
            "windowStartKind": "clock",
            "windowStartTime": 1020,
            "windowStartOffsetMin": 0,
            "windowEndKind": "none",
            "windowEndTime": null,
            "windowEndOffsetMin": 0,
            "sortOrder": 120
          },
          {
            "id": "a12",
            "name": "CGOS Ordering",
            "scheduleType": "daily",
            "dayOfWeek": null,
            "nthWeek": null,
            "regionDays": null,
            "durationType": "fixed",
            "fixedMinutes": 58.5,
            "minutesPerUnit": null,
            "minMinutes": null,
            "windowStartKind": "clock",
            "windowStartTime": 780,
            "windowStartOffsetMin": 0,
            "windowEndKind": "clock",
            "windowEndTime": 960,
            "windowEndOffsetMin": 0,
            "sortOrder": 130
          },
          {
            "id": "a13",
            "name": "EMSO Meeting",
            "scheduleType": "monthly_nth",
            "dayOfWeek": 1,
            "nthWeek": 3,
            "regionDays": null,
            "durationType": "fixed",
            "fixedMinutes": 60,
            "minutesPerUnit": null,
            "minMinutes": null,
            "windowStartKind": "none",
            "windowStartTime": null,
            "windowStartOffsetMin": 0,
            "windowEndKind": "none",
            "windowEndTime": null,
            "windowEndOffsetMin": 0,
            "sortOrder": 140
          },
          {
            "id": "a14",
            "name": "UKG End of PayPeriod",
            "scheduleType": "planning_week",
            "dayOfWeek": 0,
            "nthWeek": null,
            "regionDays": null,
            "durationType": "per_employee",
            "fixedMinutes": null,
            "minutesPerUnit": 1.32,
            "minMinutes": 20,
            "windowStartKind": "none",
            "windowStartTime": null,
            "windowStartOffsetMin": 0,
            "windowEndKind": "clock",
            "windowEndTime": 600,
            "windowEndOffsetMin": 0,
            "sortOrder": 150
          },
          {
            "id": "a15",
            "name": "UKG Rostering",
            "scheduleType": "planning_week",
            "dayOfWeek": 1,
            "nthWeek": null,
            "regionDays": null,
            "durationType": "per_employee",
            "fixedMinutes": null,
            "minutesPerUnit": 3.16,
            "minMinutes": 48,
            "windowStartKind": "none",
            "windowStartTime": null,
            "windowStartOffsetMin": 0,
            "windowEndKind": "none",
            "windowEndTime": null,
            "windowEndOffsetMin": 0,
            "sortOrder": 160
          },
          {
            "id": "a16",
            "name": "Specials Inventory Management",
            "scheduleType": "monthly_nth",
            "dayOfWeek": 1,
            "nthWeek": 2,
            "regionDays": null,
            "durationType": "fixed",
            "fixedMinutes": 210,
            "minutesPerUnit": null,
            "minMinutes": null,
            "windowStartKind": "none",
            "windowStartTime": null,
            "windowStartOffsetMin": 0,
            "windowEndKind": "none",
            "windowEndTime": null,
            "windowEndOffsetMin": 0,
            "sortOrder": 170
          },
          {
            "id": "a17",
            "name": "MyWalk",
            "scheduleType": "monthly_nth",
            "dayOfWeek": 1,
            "nthWeek": 2,
            "regionDays": null,
            "durationType": "fixed",
            "fixedMinutes": 45,
            "minutesPerUnit": null,
            "minMinutes": null,
            "windowStartKind": "none",
            "windowStartTime": null,
            "windowStartOffsetMin": 0,
            "windowEndKind": "none",
            "windowEndTime": null,
            "windowEndOffsetMin": 0,
            "sortOrder": 180
          },
          {
            "id": "a18",
            "name": "Register Assignment",
            "scheduleType": "daily",
            "dayOfWeek": null,
            "nthWeek": null,
            "regionDays": null,
            "durationType": "per_register",
            "fixedMinutes": null,
            "minutesPerUnit": 3,
            "minMinutes": null,
            "windowStartKind": "none",
            "windowStartTime": null,
            "windowStartOffsetMin": 0,
            "windowEndKind": "open",
            "windowEndTime": null,
            "windowEndOffsetMin": 0,
            "sortOrder": 190
          },
          {
            "id": "a19",
            "name": "Register Cash-Up",
            "scheduleType": "daily",
            "dayOfWeek": null,
            "nthWeek": null,
            "regionDays": null,
            "durationType": "per_register",
            "fixedMinutes": null,
            "minutesPerUnit": 5,
            "minMinutes": null,
            "windowStartKind": "close",
            "windowStartTime": null,
            "windowStartOffsetMin": -30,
            "windowEndKind": "none",
            "windowEndTime": null,
            "windowEndOffsetMin": 0,
            "sortOrder": 200
          },
          {
            "id": "a20",
            "name": "Register Date Checks",
            "scheduleType": "monthly_nth",
            "dayOfWeek": 1,
            "nthWeek": 3,
            "regionDays": null,
            "durationType": "fixed",
            "fixedMinutes": 45,
            "minutesPerUnit": null,
            "minMinutes": null,
            "windowStartKind": "none",
            "windowStartTime": null,
            "windowStartOffsetMin": 0,
            "windowEndKind": "none",
            "windowEndTime": null,
            "windowEndOffsetMin": 0,
            "sortOrder": 210
          },
          {
            "id": "a21",
            "name": "EMSO Meeting Prep",
            "scheduleType": "monthly_nth",
            "dayOfWeek": 0,
            "nthWeek": 3,
            "regionDays": null,
            "durationType": "fixed",
            "fixedMinutes": 60,
            "minutesPerUnit": null,
            "minMinutes": null,
            "windowStartKind": "none",
            "windowStartTime": null,
            "windowStartOffsetMin": 0,
            "windowEndKind": "none",
            "windowEndTime": null,
            "windowEndOffsetMin": 0,
            "sortOrder": 220
          },
          {
            "id": "a22",
            "name": "SPi Planning",
            "scheduleType": "planning_week",
            "dayOfWeek": 1,
            "nthWeek": null,
            "regionDays": null,
            "durationType": "per_employee",
            "fixedMinutes": null,
            "minutesPerUnit": 1.79,
            "minMinutes": 27,
            "windowStartKind": "none",
            "windowStartTime": null,
            "windowStartOffsetMin": 0,
            "windowEndKind": "none",
            "windowEndTime": null,
            "windowEndOffsetMin": 0,
            "sortOrder": 230
          },
          {
            "id": "a23",
            "name": "Specials Price Card Audit",
            "scheduleType": "weekly",
            "dayOfWeek": 1,
            "nthWeek": null,
            "regionDays": null,
            "durationType": "fixed",
            "fixedMinutes": 4,
            "minutesPerUnit": null,
            "minMinutes": null,
            "windowStartKind": "none",
            "windowStartTime": null,
            "windowStartOffsetMin": 0,
            "windowEndKind": "none",
            "windowEndTime": null,
            "windowEndOffsetMin": 0,
            "sortOrder": 240
          },
          {
            "id": "a24",
            "name": "STM Date Check",
            "scheduleType": "daily",
            "dayOfWeek": null,
            "nthWeek": null,
            "regionDays": null,
            "durationType": "fixed",
            "fixedMinutes": 5,
            "minutesPerUnit": null,
            "minMinutes": null,
            "windowStartKind": "clock",
            "windowStartTime": 1020,
            "windowStartOffsetMin": 0,
            "windowEndKind": "none",
            "windowEndTime": null,
            "windowEndOffsetMin": 0,
            "sortOrder": 250
          }
        ]
        """));

    /// <summary>reset_tasks catalogue, verbatim.</summary>
    public static IReadOnlyList<ResetTaskRule> ResetTasks => resetTasks.Value;

    private static readonly Lazy<List<ResetTaskRule>> resetTasks = new(() => Parse<ResetTaskRule>(
        """
        [
          {
            "id": "r1",
            "name": "Re-run 1",
            "sharePct": 15,
            "fixedMinutes": null,
            "windowStartKind": "clock",
            "windowStartTime": 600,
            "windowStartOffsetMin": 0,
            "windowEndKind": "clock",
            "windowEndTime": 840,
            "windowEndOffsetMin": 0,
            "sortOrder": 10,
            "placement": "spread"
          },
          {
            "id": "r2",
            "name": "Re-run 2",
            "sharePct": 25,
            "fixedMinutes": null,
            "windowStartKind": "clock",
            "windowStartTime": 840,
            "windowStartOffsetMin": 0,
            "windowEndKind": "clock",
            "windowEndTime": 1080,
            "windowEndOffsetMin": 0,
            "sortOrder": 20,
            "placement": "spread"
          },
          {
            "id": "r3",
            "name": "First declare (light)",
            "sharePct": 25,
            "fixedMinutes": null,
            "windowStartKind": "clock",
            "windowStartTime": 600,
            "windowStartOffsetMin": 0,
            "windowEndKind": "clock",
            "windowEndTime": 840,
            "windowEndOffsetMin": 0,
            "sortOrder": 30,
            "placement": "spread"
          },
          {
            "id": "r4",
            "name": "Full decard",
            "sharePct": null,
            "fixedMinutes": null,
            "windowStartKind": "close",
            "windowStartTime": null,
            "windowStartOffsetMin": -150,
            "windowEndKind": "close",
            "windowEndTime": null,
            "windowEndOffsetMin": 0,
            "sortOrder": 40,
            "placement": "spread"
          }
        ]
        """));

}
