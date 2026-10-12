using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;

namespace BankCore.Services.Repositories
{
    /// <summary>
    /// Generic Repository Interface
    /// Implements Repository Pattern for data access abstraction
    /// </summary>
    public interface IRepository<T> where T : class
    {
        /// <summary>
        /// Get entity by ID
        /// </summary>
        Task<T?> GetByIdAsync(int id);

        /// <summary>
        /// Get entity by ID with related entities
        /// </summary>
        Task<T?> GetByIdAsync(int id, params Expression<Func<T, object>>[] includeProperties);

        /// <summary>
        /// Get all entities
        /// </summary>
        Task<IEnumerable<T>> GetAllAsync();

        /// <summary>
        /// Get all entities with filtering
        /// </summary>
        Task<IEnumerable<T>> GetAllAsync(Expression<Func<T, bool>> filter);

        /// <summary>
        /// Get all entities with filtering and ordering
        /// </summary>
        Task<IEnumerable<T>> GetAllAsync(
            Expression<Func<T, bool>>? filter = null,
            Func<IQueryable<T>, IOrderedQueryable<T>>? orderBy = null,
            params Expression<Func<T, object>>[] includeProperties);

        /// <summary>
        /// Find entities matching predicate
        /// </summary>
        Task<IEnumerable<T>> FindAsync(Expression<Func<T, bool>> predicate);

        /// <summary>
        /// Find single entity matching predicate
        /// </summary>
        Task<T?> FindSingleAsync(Expression<Func<T, bool>> predicate);

        /// <summary>
        /// Find single entity with related entities
        /// </summary>
        Task<T?> FindSingleAsync(
            Expression<Func<T, bool>> predicate,
            params Expression<Func<T, object>>[] includeProperties);

        /// <summary>
        /// Check if any entity matches predicate
        /// </summary>
        Task<bool> AnyAsync(Expression<Func<T, bool>> predicate);

        /// <summary>
        /// Count entities matching predicate
        /// </summary>
        Task<int> CountAsync(Expression<Func<T, bool>>? predicate = null);

        /// <summary>
        /// Add new entity
        /// </summary>
        Task<T> AddAsync(T entity);

        /// <summary>
        /// Add multiple entities
        /// </summary>
        Task<IEnumerable<T>> AddRangeAsync(IEnumerable<T> entities);

        /// <summary>
        /// Update existing entity
        /// </summary>
        Task UpdateAsync(T entity);

        /// <summary>
        /// Update multiple entities
        /// </summary>
        Task UpdateRangeAsync(IEnumerable<T> entities);

        /// <summary>
        /// Delete entity
        /// </summary>
        Task DeleteAsync(T entity);

        /// <summary>
        /// Delete multiple entities
        /// </summary>
        Task DeleteRangeAsync(IEnumerable<T> entities);

        /// <summary>
        /// Execute raw SQL query
        /// </summary>
        Task<IEnumerable<T>> ExecuteSqlQueryAsync(string sql, params object[] parameters);

        /// <summary>
        /// Execute raw SQL command
        /// </summary>
        Task<int> ExecuteSqlCommandAsync(string sql, params object[] parameters);
    }

    /// <summary>
    /// Repository with pagination support
    /// </summary>
    public interface IPaginatedRepository<T> : IRepository<T> where T : class
    {
        /// <summary>
        /// Get paginated results
        /// </summary>
        Task<PaginatedResult<T>> GetPagedAsync(
            int pageNumber,
            int pageSize,
            Expression<Func<T, bool>>? filter = null,
            Func<IQueryable<T>, IOrderedQueryable<T>>? orderBy = null);

        /// <summary>
        /// Get paginated results with cursor
        /// </summary>
        Task<CursorPaginatedResult<T>> GetCursorPagedAsync(
            string? cursor,
            int pageSize,
            Expression<Func<T, bool>>? filter = null,
            Func<IQueryable<T>, IOrderedQueryable<T>>? orderBy = null);
    }

    /// <summary>
    /// Paginated result
    /// </summary>
    public class PaginatedResult<T>
    {
        public IEnumerable<T> Items { get; set; } = new List<T>();
        public int TotalCount { get; set; }
        public int PageNumber { get; set; }
        public int PageSize { get; set; }
        public int TotalPages => (int)Math.Ceiling((double)TotalCount / PageSize);
        public bool HasPrevious => PageNumber > 1;
        public bool HasNext => PageNumber < TotalPages;
    }

    /// <summary>
    /// Cursor paginated result
    /// </summary>
    public class CursorPaginatedResult<T>
    {
        public IEnumerable<T> Items { get; set; } = new List<T>();
        public string? NextCursor { get; set; }
        public string? PreviousCursor { get; set; }
        public bool HasMore { get; set; }
    }
}
