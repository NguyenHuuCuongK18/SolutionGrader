using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace SolutionGrader.Services
{
    public sealed class DatabaseResetService
    {
        private static readonly Regex GoRegex = new("^\\s*GO\\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);

        private readonly string _connectionString;
        private readonly string _scriptPath;

        public DatabaseResetService(string connectionString, string scriptPath)
        {
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            _scriptPath = scriptPath ?? throw new ArgumentNullException(nameof(scriptPath));
        }

        public async Task ResetAsync(CancellationToken cancellationToken)
        {
            if (!File.Exists(_scriptPath))
            {
                throw new FileNotFoundException("Database script file not found.", _scriptPath);
            }

            var builder = new SqlConnectionStringBuilder(_connectionString);
            if (string.IsNullOrWhiteSpace(builder.InitialCatalog))
            {
                throw new InvalidOperationException("Connection string does not specify a database name (Initial Catalog).");
            }

            var databaseName = builder.InitialCatalog;
            await DropDatabaseAsync(builder, databaseName, cancellationToken).ConfigureAwait(false);
            await CreateDatabaseAsync(builder, databaseName, cancellationToken).ConfigureAwait(false);
            await ApplyScriptAsync(builder, databaseName, cancellationToken).ConfigureAwait(false);
        }

        private static async Task DropDatabaseAsync(SqlConnectionStringBuilder builder, string databaseName, CancellationToken token)
        {
            using var connection = new SqlConnection(BuildMasterConnectionString(builder));
            await connection.OpenAsync(token).ConfigureAwait(false);

            var commandText = $@"
IF EXISTS (SELECT name FROM sys.databases WHERE name = @name)
BEGIN
    ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [{databaseName}];
END";

            using var command = new SqlCommand(commandText, connection)
            {
                CommandType = CommandType.Text
            };
            command.Parameters.AddWithValue("@name", databaseName);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }

        private static async Task CreateDatabaseAsync(SqlConnectionStringBuilder builder, string databaseName, CancellationToken token)
        {
            using var connection = new SqlConnection(BuildMasterConnectionString(builder));
            await connection.OpenAsync(token).ConfigureAwait(false);

            using var command = new SqlCommand($"CREATE DATABASE [{databaseName}]", connection)
            {
                CommandType = CommandType.Text
            };

            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }

        private async Task ApplyScriptAsync(SqlConnectionStringBuilder builder, string databaseName, CancellationToken token)
        {
            var script = await File.ReadAllTextAsync(_scriptPath, token).ConfigureAwait(false);
            var batches = SplitSqlBatches(script);

            builder.InitialCatalog = databaseName;
            using var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync(token).ConfigureAwait(false);

            foreach (var batch in batches)
            {
                using var command = new SqlCommand(batch, connection)
                {
                    CommandType = CommandType.Text
                };

                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }

        private static IEnumerable<string> SplitSqlBatches(string script)
        {
            return GoRegex.Split(script)
                .Select(batch => batch.Trim())
                .Where(batch => !string.IsNullOrWhiteSpace(batch));
        }

        private static string BuildMasterConnectionString(SqlConnectionStringBuilder builder)
        {
            var masterBuilder = new SqlConnectionStringBuilder(builder.ConnectionString)
            {
                InitialCatalog = "master"
            };

            return masterBuilder.ConnectionString;
        }
    }
}