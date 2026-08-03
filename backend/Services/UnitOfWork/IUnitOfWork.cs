using System;
using System.Threading.Tasks;
using BankCore.Data;
using BankCore.Services.Repositories;

namespace BankCore.Services.UnitOfWork
{
    /// <summary>
    /// Unit of Work Interface
    /// Manages transactions and repository coordination
    /// </summary>
    public interface IUnitOfWork : IDisposable
    {
        /// <summary>
        /// Get repository for entity type
        /// </summary>
        IRepository<T> Repository<T>() where T : class;

        /// <summary>
        /// Get paginated repository for entity type
        /// </summary>
        IPaginatedRepository<T> PaginatedRepository<T>() where T : class;

        /// <summary>
        /// Begin transaction
        /// </summary>
        Task BeginTransactionAsync();

        /// <summary>
        /// Commit transaction
        /// </summary>
        Task CommitAsync();

        /// <summary>
        /// Rollback transaction
        /// </summary>
        Task RollbackAsync();

        /// <summary>
        /// Save changes
        /// </summary>
        Task<int> SaveChangesAsync();

        /// <summary>
        /// Check if transaction is active
        /// </summary>
        bool HasActiveTransaction { get; }
    }

    /// <summary>
    /// Unit of Work Implementation
    /// </summary>
    public class UnitOfWork : IUnitOfWork
    {
        private readonly ApplicationDbContext _context;
        private readonly Dictionary<Type, object> _repositories = new();
        private readonly Dictionary<Type, object> _paginatedRepositories = new();
        private bool _disposed;

        public UnitOfWork(ApplicationDbContext context)
        {
            _context = context;
        }

        public IRepository<T> Repository<T>() where T : class
        {
            if (_repositories.ContainsKey(typeof(T)))
            {
                return (IRepository<T>)_repositories[typeof(T)];
            }

            var repository = new Repository<T>(_context);
            _repositories[typeof(T)] = repository;
            return repository;
        }

        public IPaginatedRepository<T> PaginatedRepository<T>() where T : class
        {
            if (_paginatedRepositories.ContainsKey(typeof(T)))
            {
                return (IPaginatedRepository<T>)_paginatedRepositories[typeof(T)];
            }

            var repository = new PaginatedRepository<T>(_context);
            _paginatedRepositories[typeof(T)] = repository;
            return repository;
        }

        public async Task BeginTransactionAsync()
        {
            await _context.Database.BeginTransactionAsync();
        }

        public async Task CommitAsync()
        {
            try
            {
                await _context.Database.CommitTransactionAsync();
            }
            catch
            {
                await RollbackAsync();
                throw;
            }
        }

        public async Task RollbackAsync()
        {
            await _context.Database.RollbackTransactionAsync();
        }

        public async Task<int> SaveChangesAsync()
        {
            return await _context.SaveChangesAsync();
        }

        public bool HasActiveTransaction => _context.Database.CurrentTransaction != null;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _context.Dispose();
                }
                _disposed = true;
            }
        }
    }
}
