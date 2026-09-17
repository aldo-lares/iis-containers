using System.Data.Common;

namespace Shared.Contracts;

public static class StorefrontDefaults
{
    public static string FrontendUrl { get; } = BuildLocalUrl(5000);
    public static string CatalogApiUrl { get; } = BuildLocalUrl(5001);
    public static string OrdersApiUrl { get; } = BuildLocalUrl(5002);

    private static string BuildLocalUrl(int port) => new UriBuilder(Uri.UriSchemeHttp, "localhost", port).Uri.ToString().TrimEnd('/');
}

public static class SqliteDatabaseConfiguration
{
    public static string GetConnectionString(string? configuredConnectionString, string serviceFolder, string fileName)
    {
        if (string.IsNullOrWhiteSpace(configuredConnectionString))
        {
            var dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StorefrontPoC", serviceFolder, fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            return $"Data Source={dbPath}";
        }

        EnsureDataSourceDirectory(configuredConnectionString);
        return configuredConnectionString;
    }

    private static void EnsureDataSourceDirectory(string connectionString)
    {
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        if (!builder.TryGetValue("Data Source", out var dataSourceValue) &&
            !builder.TryGetValue("DataSource", out dataSourceValue))
        {
            return;
        }

        var dataSource = dataSourceValue?.ToString();
        if (string.IsNullOrWhiteSpace(dataSource) ||
            dataSource == ":memory:" ||
            dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(dataSource));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }
}
