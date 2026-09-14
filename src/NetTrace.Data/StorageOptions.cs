namespace NetTrace.Data;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>Where the usual app keeps everything: %LocalAppData%\NetTrace.</summary>
    public static string UserDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetTrace");

    /// <summary>
    /// Where the NetTrace service keeps its data, and where windows read it while the service is
    /// installed: %ProgramData%\NetTrace, which the service installer opens to every user of the PC.
    /// </summary>
    public static string SharedDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NetTrace");

    /// <summary>Defaults to <see cref="UserDirectory"/>. Everything the app writes lives here, apart from preferences.</summary>
    public string DataDirectory { get; set; } = UserDirectory;

    public string DatabasePath => Path.Combine(DataDirectory, "nettrace.db");

    public string LogDirectory => Path.Combine(DataDirectory, "logs");

    public string ConnectionString =>
        $"Data Source={DatabasePath};Cache=Shared;Pooling=True";
}
