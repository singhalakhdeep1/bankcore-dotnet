// RavenDB service for bankcore-dotnet

using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Linq;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;

namespace BankingAPI.Services
{
    public class RavenDBService
    {
        private readonly IDocumentStore _store;

        public RavenDBService(IConfiguration configuration)
        {
            var urls = configuration.GetSection("RavenDB:Urls").Get<string[]>() ?? new[] { "http://localhost:8080" };
            var database = configuration.GetSection("RavenDB:Database").Get<string>() ?? "bankcore";

            _store = new DocumentStore
            {
                Urls = urls,
                Database = database
            };
            _store.Initialize();
            EnsureDatabaseExists(database);
        }

        /// <summary>Creates the database if the server does not have it yet, so a fresh RavenDB works without manual setup.</summary>
        private void EnsureDatabaseExists(string database)
        {
            try
            {
                _store.Maintenance.Server.Send(new CreateDatabaseOperation(new DatabaseRecord(database)));
            }
            catch (Exception)
            {
                // Already exists, or the server is not reachable yet; the first real call reports any genuine problem.
            }
        }

        public IAsyncDocumentSession OpenSession()
        {
            return _store.OpenAsyncSession();
        }

        public async Task<T?> LoadAsync<T>(string id)
        {
            using var session = OpenSession();
            return await session.LoadAsync<T>(id);
        }

        public async Task<List<T>> LoadAsync<T>(IEnumerable<string> ids)
        {
            using var session = OpenSession();
            return await session.LoadAsync<T>(ids).ContinueWith(t => t.Result.Values.ToList());
        }

        public async Task StoreAsync<T>(T entity)
        {
            using var session = OpenSession();
            await session.StoreAsync(entity);
            await session.SaveChangesAsync();
        }

        public async Task DeleteAsync<T>(string id)
        {
            using var session = OpenSession();
            var entity = await session.LoadAsync<T>(id);
            if (entity != null)
            {
                session.Delete(id);
                await session.SaveChangesAsync();
            }
        }

        public async Task<List<T>> QueryAsync<T>(Func<IAsyncDocumentSession, IRavenQueryable<T>> queryFunc)
        {
            using var session = OpenSession();
            var query = queryFunc(session);
            return await query.ToListAsync();
        }

        public async Task<bool> TestConnectionAsync()
        {
            try
            {
                using var session = OpenSession();
                await session.Query<object>().Take(1).ToListAsync();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public async Task CreateIndexAsync<T, TIndex>() where TIndex : AbstractIndexCreationTask, new()
        {
            var index = new TIndex();
            await _store.ExecuteIndexAsync(index);
        }

        public async Task BulkInsertAsync<T>(IEnumerable<T> entities)
        {
            using var bulkInsert = _store.BulkInsert();
            foreach (var entity in entities)
            {
                await bulkInsert.StoreAsync(entity);
            }
        }

        public void Dispose()
        {
            _store?.Dispose();
        }
    }

}
