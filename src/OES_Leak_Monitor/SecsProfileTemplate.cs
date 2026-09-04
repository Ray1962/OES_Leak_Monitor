using System;
using System.IO;

namespace OES_Leak_Monitor;

/// <summary>
/// The device profile this app ships with, and the rules for getting it onto disk.
/// <para/>
/// The profile is the site-editable half of the SECS interface: SVID numbers, display
/// names, units and alarm texts live there, and the app only supplies the values behind
/// them (bound by name — see <see cref="SecsBridge"/>). A customer renumbering their
/// SVIDs edits one file; nothing is rebuilt.
/// <para/>
/// It is written into the config folder on first run rather than shipped next to the
/// .exe, because an installation under <c>Program Files</c> is read-only and a profile
/// nobody can edit defeats the point of having one.
/// </summary>
public static class SecsProfileTemplate
{
    /// <summary>Sub-folder of <c>OesAppPaths.ConfigDirectory</c> holding profiles.</summary>
    public const string FolderName = "profiles";

    /// <summary>
    /// Sub-folder holding the chamber-stamped profile the equipment actually loads. Derived
    /// output: rewritten from the template on every start, never edited by hand.
    /// </summary>
    public const string EffectiveFolderName = ".effective";

    /// <summary>
    /// Writes the template to <paramref name="path"/> if nothing is there yet, and returns
    /// whether it wrote. An existing file is never overwritten — it may carry site edits.
    /// </summary>
    public static bool EnsureExists(string path)
    {
        if (File.Exists(path))
        {
            return false;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Json);
        return true;
    }

    /// <summary>
    /// The template, with <c>cc = 00</c> throughout. 00 is not a chamber
    /// (<see cref="SecsChamberCoding.IsValidChamber"/> rejects it), which is the point:
    /// a profile that was never stamped cannot be mistaken for a stamped one.
    /// </summary>
    public const string Json = """
{
  // OES Leak Monitor SECS device profile.
  //
  // Ids are written with cc = 00 and re-stamped at start-up for the chamber set on the
  // SECS tab (SVID = 1 cc 27 00 vvv, ALID/CEID = 1 cc 27 nnn). Field semantics are in
  // docs/Satellite_SECS_Specification_v2.md; this app's side is docs/secs-integration.md.
  //
  // "bind" is the contract with the program: the numbers and names are yours to change,
  // the bind name is what the code answers to. A bind name the app does not supply is
  // reported the moment the interface starts, not when a host first asks for it.
  "name": "OES Leak Monitor (ss=27)",

  "statusVariables": [
    { "svid": 1002700001, "name": "Leak rate",                    "units": "mbar-L/s", "format": "F4", "bind": "oes.leakRate" },
    { "svid": 1002700002, "name": "Leak rate sigma",              "units": "mbar-L/s", "format": "F4", "bind": "oes.leakRateSigma" },
    { "svid": 1002700003, "name": "Leak rate confidence",         "units": "",         "format": "F4", "bind": "oes.leakRateConfidence" },
    { "svid": 1002700004, "name": "Leak rate valid",              "units": "",         "format": "U4", "bind": "oes.leakRateValid" },
    { "svid": 1002700005, "name": "Out of calibrated range",      "units": "",         "format": "U4", "bind": "oes.outOfCalibratedRange" },
    { "svid": 1002700006, "name": "Calibration status",           "units": "",         "format": "U4", "bind": "oes.calibrationStatus" },
    { "svid": 1002700007, "name": "Composite leak level",         "units": "",         "format": "U4", "bind": "oes.compositeLevel" },
    { "svid": 1002700008, "name": "Enabled ratio count",          "units": "",         "format": "U4", "bind": "oes.enabledRatios" },
    { "svid": 1002700009, "name": "Warning ratio count",          "units": "",         "format": "U4", "bind": "oes.warningRatios" },
    { "svid": 1002700010, "name": "Alarm ratio count",            "units": "",         "format": "U4", "bind": "oes.alarmRatios" },
    { "svid": 1002700011, "name": "Low-signal ratio count",       "units": "",         "format": "U4", "bind": "oes.lowSignalRatios" },
    { "svid": 1002700012, "name": "Baseline available",           "units": "",         "format": "U4", "bind": "oes.baselineAvailable" },
    { "svid": 1002700013, "name": "Active Golden Run name",       "units": "",         "format": "A",  "bind": "oes.goldenRunName" },
    { "svid": 1002700014, "name": "Active calibration name",      "units": "",         "format": "A",  "bind": "oes.calibrationName" },
    { "svid": 1002700015, "name": "Acquisition mismatch",         "units": "",         "format": "U4", "bind": "oes.acquisitionMismatch" },
    { "svid": 1002700016, "name": "Test / replay mode",           "units": "",         "format": "U4", "bind": "oes.testMode" },
    { "svid": 1002700017, "name": "Golden Run capture active",    "units": "",         "format": "U4", "bind": "oes.captureActive" },
    { "svid": 1002700018, "name": "Golden Run capture progress",  "units": "%",        "format": "F4", "bind": "oes.captureProgress" },
    { "svid": 1002700019, "name": "Calibration capture active",   "units": "",         "format": "U4", "bind": "oes.calCaptureActive" },
    { "svid": 1002700020, "name": "Calibration capture progress", "units": "%",        "format": "F4", "bind": "oes.calCaptureProgress" },
    { "svid": 1002700021, "name": "Plasma present",               "units": "",         "format": "U4", "bind": "oes.plasmaPresent" },
    { "svid": 1002700022, "name": "Plasma gate available",        "units": "",         "format": "U4", "bind": "oes.plasmaGateAvailable" },
    { "svid": 1002700023, "name": "Frame dropout count",          "units": "",         "format": "U4", "bind": "oes.dropoutCount" },
    { "svid": 1002700024, "name": "Integration time",             "units": "ms",       "format": "F4", "bind": "oes.integrationTime" },
    { "svid": 1002700025, "name": "Average count",                "units": "",         "format": "U4", "bind": "oes.averageCount" },
    { "svid": 1002700026, "name": "Frame rate",                   "units": "Hz",       "format": "F4", "bind": "oes.frameRate" },

    // 027-029: which process the running plasma step is. 028 is what makes 027 readable --
    // the name is blank for "no classifier", "no step" and "not decided yet" alike.
    { "svid": 1002700027, "name": "Process class",               "units": "",         "format": "A",  "bind": "oes.processClass" },
    { "svid": 1002700028, "name": "Process class state",         "units": "",         "format": "U4", "bind": "oes.processClassState" },
    { "svid": 1002700029, "name": "Process step index",          "units": "",         "format": "U4", "bind": "oes.processStepIndex" }
  ],

  // category follows SEMI E5: 4 = parameter control error, 5 = irrecoverable error,
  // 6 = equipment status warning, 8 = data integrity.
  // The text here is what S5F5 lists; a raised alarm carries a fuller one with live values.
  "alarms": [
    { "alid": 10027001, "category": 6, "text": "OES LEAK WARNING" },
    { "alid": 10027002, "category": 4, "text": "OES LEAK ALARM" },
    { "alid": 10027012, "category": 5, "text": "OES CONNECTION LOST" },
    { "alid": 10027013, "category": 5, "text": "OES ACQUISITION ERROR" },
    { "alid": 10027014, "category": 8, "text": "OES DATA WRITE FAILURE" }
  ],

  // No remote commands: this tool reports, it is not driven from the host.
  // A host S2F41 is answered HCACK=1 (command not recognised).
  "remoteCommands": {},

  "hostActions": [],
  "equipmentActions": []
}
""";
}
