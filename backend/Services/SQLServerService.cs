// SQL Server service for bankcore-dotnet

using Microsoft.Data.SqlClient;
using System.Data;

namespace BankingAPI.Services
{
    public class SQLServerService
    {
        private readonly string _connectionString;

        public SQLServerService(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("SQLServer") ?? 
                "Server=localhost;Database=bankcore;User Id=sa;Password=YourPassword123;TrustServerCertificate=True;";
        }

        private SqlConnection GetConnection()
        {
            return new SqlConnection(_connectionString);
        }

        public async Task<DataTable> ExecuteQueryAsync(string query, Dictionary<string, object>? parameters = null)
        {
            using var connection = GetConnection();
            await connection.OpenAsync();

            using var command = new SqlCommand(query, connection);
            command.CommandType = CommandType.Text;
            
            if (parameters != null)
            {
                foreach (var param in parameters)
                {
                    command.Parameters.AddWithValue(param.Key, param.Value);
                }
            }

            using var adapter = new SqlDataAdapter(command);
            var dataTable = new DataTable();
            adapter.Fill(dataTable);
            
            return dataTable;
        }

        public async Task<int> ExecuteNonQueryAsync(string query, Dictionary<string, object>? parameters = null)
        {
            using var connection = GetConnection();
            await connection.OpenAsync();

            using var command = new SqlCommand(query, connection);
            command.CommandType = CommandType.Text;
            
            if (parameters != null)
            {
                foreach (var param in parameters)
                {
                    command.Parameters.AddWithValue(param.Key, param.Value);
                }
            }

            return await command.ExecuteNonQueryAsync();
        }

        public async Task<object?> ExecuteScalarAsync(string query, Dictionary<string, object>? parameters = null)
        {
            using var connection = GetConnection();
            await connection.OpenAsync();

            using var command = new SqlCommand(query, connection);
            command.CommandType = CommandType.Text;
            
            if (parameters != null)
            {
                foreach (var param in parameters)
                {
                    command.Parameters.AddWithValue(param.Key, param.Value);
                }
            }

            return await command.ExecuteScalarAsync();
        }

        public async Task<SqlDataReader> ExecuteReaderAsync(string query, Dictionary<string, object>? parameters = null)
        {
            var connection = GetConnection();
            await connection.OpenAsync();

            var command = new SqlCommand(query, connection);
            command.CommandType = CommandType.Text;
            
            if (parameters != null)
            {
                foreach (var param in parameters)
                {
                    command.Parameters.AddWithValue(param.Key, param.Value);
                }
            }

            return await command.ExecuteReaderAsync(CommandBehavior.CloseConnection);
        }

        public async Task<bool> TestConnectionAsync()
        {
            try
            {
                using var connection = GetConnection();
                await connection.OpenAsync();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public async Task<SqlTransaction> BeginTransactionAsync()
        {
            var connection = GetConnection();
            await connection.OpenAsync();
            return await connection.BeginTransactionAsync();
        }

        public async Task ExecuteStoredProcedureAsync(string procedureName, Dictionary<string, object>? parameters = null)
        {
            using var connection = GetConnection();
            await connection.OpenAsync();

            using var command = new SqlCommand(procedureName, connection);
            command.CommandType = CommandType.StoredProcedure;
            
            if (parameters != null)
            {
                foreach (var param in parameters)
                {
                    command.Parameters.AddWithValue(param.Key, param.Value);
                }
            }

            await command.ExecuteNonQueryAsync();
        }
    }
}
