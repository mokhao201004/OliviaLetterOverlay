namespace OliviaLetterOverlay;

// Linked production sources run against a temporary user directory and synthetic keys.
// This type exists only in the test assembly; the shipping application uses System.Environment.
internal static class Environment
{
    public static readonly string TestRoot = System.Environment.GetEnvironmentVariable("OLIVIA_TEST_ROOT")
        ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "OliviaLetterOverlay-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly Dictionary<(string, EnvironmentVariableTarget), string?> Variables = [];
    public enum SpecialFolder { LocalApplicationData, UserProfile }
    public static string GetFolderPath(SpecialFolder folder) => System.IO.Path.Combine(TestRoot,
        folder == SpecialFolder.LocalApplicationData ? "AppData" : "User");
    public static string? GetEnvironmentVariable(string name, EnvironmentVariableTarget target = EnvironmentVariableTarget.Process) =>
        Variables.GetValueOrDefault((name, target));
    public static void SetEnvironmentVariable(string name, string? value, EnvironmentVariableTarget target) => Variables[(name, target)] = value;
    public static string[] GetCommandLineArgs() => System.Environment.GetCommandLineArgs();
    public static string NewLine => System.Environment.NewLine;
    public static string CurrentDirectory => System.Environment.CurrentDirectory;
    public static Version Version => System.Environment.Version;
    public static OperatingSystem OSVersion => System.Environment.OSVersion;
    public static bool Is64BitProcess => System.Environment.Is64BitProcess;
}
