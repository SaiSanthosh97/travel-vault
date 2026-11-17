using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TravelVault.Core.Common;

namespace TravelVault.Storage.Sqlite;

internal static class SqlHelpers
{
    public static SqliteCommand Command(this SqliteConnection connection, string sql, SqliteTransaction? transaction = null, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    public static int Run(this SqliteConnection connection, string sql, SqliteTransaction? transaction = null, params (string, object?)[] parameters)
    {
        using var command = connection.Command(sql, transaction, parameters);
        return command.ExecuteNonQuery();
    }

    public static List<T> Query<T>(this SqliteConnection connection, string sql, Func<SqliteDataReader, T> map, params (string, object?)[] parameters)
    {
        using var command = connection.Command(sql, null, parameters);
        using var reader = command.ExecuteReader();
        var list = new List<T>();
        while (reader.Read())
        {
            list.Add(map(reader));
        }

        return list;
    }

    public static string ToJson<T>(T value) => JsonSerializer.Serialize(value, JsonDefaults.Options);

    public static T FromJson<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, JsonDefaults.Options) ?? throw new InvalidDataException("Empty record.");

    /// <summary>Sortable UTC text (ISO-8601, fixed width).</summary>
    public static string? Utc(DateTimeOffset? value) =>
        value?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public static int Bool(bool value) => value ? 1 : 0;
}
