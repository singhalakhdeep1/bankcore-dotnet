// MongoDB service for bankcore-dotnet

using MongoDB.Bson;
using MongoDB.Driver;

namespace BankingAPI.Services
{
    public class MongoDBService
    {
        private readonly IMongoDatabase _database;

        public MongoDBService(IConfiguration configuration)
        {
            var connectionString = configuration.GetConnectionString("MongoDB") ?? 
                "mongodb://localhost:27017";
            var client = new MongoClient(connectionString);
            _database = client.GetDatabase("bankcore");
        }

        public IMongoCollection<T> GetCollection<T>(string collectionName)
        {
            return _database.GetCollection<T>(collectionName);
        }

        public async Task<List<T>> FindAsync<T>(string collectionName, FilterDefinition<T> filter)
        {
            var collection = GetCollection<T>(collectionName);
            return await collection.Find(filter).ToListAsync();
        }

        public async Task<T?> FindOneAsync<T>(string collectionName, FilterDefinition<T> filter)
        {
            var collection = GetCollection<T>(collectionName);
            return await collection.Find(filter).FirstOrDefaultAsync();
        }

        public async Task InsertOneAsync<T>(string collectionName, T document)
        {
            var collection = GetCollection<T>(collectionName);
            await collection.InsertOneAsync(document);
        }

        public async Task InsertManyAsync<T>(string collectionName, IEnumerable<T> documents)
        {
            var collection = GetCollection<T>(collectionName);
            await collection.InsertManyAsync(documents);
        }

        public async Task UpdateOneAsync<T>(string collectionName, FilterDefinition<T> filter, UpdateDefinition<T> update)
        {
            var collection = GetCollection<T>(collectionName);
            await collection.UpdateOneAsync(filter, update);
        }

        public async Task UpdateManyAsync<T>(string collectionName, FilterDefinition<T> filter, UpdateDefinition<T> update)
        {
            var collection = GetCollection<T>(collectionName);
            await collection.UpdateManyAsync(filter, update);
        }

        public async Task DeleteOneAsync<T>(string collectionName, FilterDefinition<T> filter)
        {
            var collection = GetCollection<T>(collectionName);
            await collection.DeleteOneAsync(filter);
        }

        public async Task DeleteManyAsync<T>(string collectionName, FilterDefinition<T> filter)
        {
            var collection = GetCollection<T>(collectionName);
            await collection.DeleteManyAsync(filter);
        }

        public async Task<long> CountAsync<T>(string collectionName, FilterDefinition<T> filter)
        {
            var collection = GetCollection<T>(collectionName);
            return await collection.CountDocumentsAsync(filter);
        }

        public async Task<bool> TestConnectionAsync()
        {
            try
            {
                await _database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1));
                return true;
            }
            catch
            {
                return false;
            }
        }

        public async Task CreateIndexAsync<T>(string collectionName, CreateIndexModel<T> indexModel)
        {
            var collection = GetCollection<T>(collectionName);
            await collection.Indexes.CreateOneAsync(indexModel);
        }

        public async Task AggregateAsync<T>(string collectionName, PipelineDefinition<T, T> pipeline)
        {
            var collection = GetCollection<T>(collectionName);
            await collection.AggregateAsync(pipeline);
        }
    }
}
