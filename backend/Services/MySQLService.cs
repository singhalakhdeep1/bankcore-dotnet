// MySQL database service for bankcore-dotnet

using MySql.Data.MySqlClient;
using System.Data;

namespace BankingAPI.Services
{
    public class MySQLService
    {
        private readonly string _connectionString;

        public MySQLService(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("MySQL") ?? 
                "Server=localhost;Database=bankcore;User=root;Password=password;";
        }

        private MySqlConnection GetConnection()
        {
            return new MySqlConnection(_connectionString);
        }

        public async Task<DataTable> ExecuteQueryAsync(string query, Dictionary<string, object>? parameters = null)
        {
            using var connection = GetConnection();
            await connection.OpenAsync();

            using var command = new MySqlCommand(query, connection);
            
            if (parameters != null)
            {
                foreach (var param in parameters)
                {
                    command.Parameters.AddWithValue(param.Key, param.Value);
                }
            }

            using var adapter = new MySqlDataAdapter(command);
            var dataTable = new DataTable();
            adapter.Fill(dataTable);
            
            return dataTable;
        }

        public async Task<int> ExecuteNonQueryAsync(string query, Dictionary<string, object>? parameters = null)
        {
            using var connection = GetConnection();
            await connection.OpenAsync();

            using var command = new MySqlCommand(query, connection);
            
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

            using var command = new MySqlCommand(query, connection);
            
            if (parameters != null)
            {
                foreach (var param in parameters)
                {
                    command.Parameters.AddWithValue(param.Key, param.Value);
                }
            }

            return await command.ExecuteScalarAsync();
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

        public async Task BeginTransactionAsync()
        {
            using var connection = GetConnection();
            await connection.OpenAsync();
            var transaction = await connection.BeginTransactionAsync();
        }
    }
}
