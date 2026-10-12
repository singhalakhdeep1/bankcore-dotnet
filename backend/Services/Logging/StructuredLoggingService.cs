using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace BankCore.Services.Logging
{
    /// <summary>
    /// Structured Logging Service for Financial Audit Logs
    /// Implements structured logging with correlation IDs and context
    /// </summary>
    public class StructuredLoggingService
    {
        private readonly ILogger<StructuredLoggingService> _logger;
        private readonly AsyncLocal<LogContext> _currentContext = new();

        public StructuredLoggingService(ILogger<StructuredLoggingService> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Set current log context
        /// </summary>
        public void SetContext(LogContext context)
        {
            _currentContext.Value = context;
        }

        /// <summary>
        /// Get current log context
        /// </summary>
        public LogContext? GetContext()
        {
            return _currentContext.Value;
        }

        /// <summary>
        /// Log information with structured context
        /// </summary>
        public void LogInformation(string message, Dictionary<string, object>? additionalContext = null)
        {
            var logEntry = CreateLogEntry(LogLevel.Information, message, additionalContext);
            _logger.LogInformation("{@LogEntry}", logEntry);
        }

        /// <summary>
        /// Log warning with structured context
        /// </summary>
        public void LogWarning(string message, Dictionary<string, object>? additionalContext = null)
        {
            var logEntry = CreateLogEntry(LogLevel.Warning, message, additionalContext);
            _logger.LogWarning("{@LogEntry}", logEntry);
        }

        /// <summary>
        /// Log error with structured context
        /// </summary>
        public void LogError(string message, Exception? exception = null, Dictionary<string, object>? additionalContext = null)
        {
            var logEntry = CreateLogEntry(LogLevel.Error, message, additionalContext, exception);
            _logger.LogError(exception, "{@LogEntry}", logEntry);
        }

        /// <summary>
        /// Log audit event for compliance
        /// </summary>
        public void LogAuditEvent(AuditEventType eventType, string description, Dictionary<string, object>? additionalContext = null)
        {
            var context = additionalContext ?? new Dictionary<string, object>();
            context["AuditEventType"] = eventType.ToString();
            context["AuditDescription"] = description;

            var logEntry = CreateLogEntry(LogLevel.Information, $"AUDIT: {description}", context);
            _logger.LogInformation("{@LogEntry}", logEntry);
        }

        /// <summary>
        /// Log transaction event
        /// </summary>
        public void LogTransactionEvent(
            string transactionId,
            TransactionEventType eventType,
            string description,
            Dictionary<string, object>? additionalContext = null)
        {
            var context = additionalContext ?? new Dictionary<string, object>();
            context["TransactionId"] = transactionId;
            context["TransactionEventType"] = eventType.ToString();
            context["TransactionDescription"] = description;

            var logEntry = CreateLogEntry(LogLevel.Information, $"TRANSACTION: {description}", context);
            _logger.LogInformation("{@LogEntry}", logEntry);
        }

        /// <summary>
        /// Create log entry with context
        /// </summary>
        private LogEntry CreateLogEntry(
            LogLevel level,
            string message,
            Dictionary<string, object>? additionalContext = null,
            Exception? exception = null)
        {
            var context = _currentContext.Value ?? new LogContext();
            
            var logEntry = new LogEntry
            {
                Timestamp = DateTime.UtcNow,
                Level = level.ToString(),
                Message = message,
                CorrelationId = context.CorrelationId,
                UserId = context.UserId,
                AccountId = context.AccountId,
                RequestId = context.RequestId,
                IpAddress = context.IpAddress,
                UserAgent = context.UserAgent,
                AdditionalContext = additionalContext,
                Exception = exception != null ? new ExceptionInfo(exception) : null
            };

            return logEntry;
        }

        /// <summary>
        /// Create new correlation ID
        /// </summary>
        public static string CreateCorrelationId()
        {
            return Guid.NewGuid().ToString();
        }
    }

    /// <summary>
    /// Log context for request tracking
    /// </summary>
    public class LogContext
    {
        public string CorrelationId { get; set; } = Guid.NewGuid().ToString();
        public string? UserId { get; set; }
        public string? AccountId { get; set; }
        public string? RequestId { get; set; }
        public string? IpAddress { get; set; }
        public string? UserAgent { get; set; }
        public Dictionary<string, object>? CustomProperties { get; set; }
    }

    /// <summary>
    /// Structured log entry
    /// </summary>
    public class LogEntry
    {
        public DateTime Timestamp { get; set; }
        public string Level { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string CorrelationId { get; set; } = string.Empty;
        public string? UserId { get; set; }
        public string? AccountId { get; set; }
        public string? RequestId { get; set; }
        public string? IpAddress { get; set; }
        public string? UserAgent { get; set; }
        public Dictionary<string, object>? AdditionalContext { get; set; }
        public ExceptionInfo? Exception { get; set; }
    }

    /// <summary>
    /// Exception information for logging
    /// </summary>
    public class ExceptionInfo
    {
        public string Message { get; set; }
        public string? StackTrace { get; set; }
        public string? Type { get; set; }
        public ExceptionInfo? InnerException { get; set; }

        public ExceptionInfo(Exception exception)
        {
            Message = exception.Message;
            StackTrace = exception.StackTrace;
            Type = exception.GetType().Name;
            InnerException = exception.InnerException != null 
                ? new ExceptionInfo(exception.InnerException) 
                : null;
        }
    }

    /// <summary>
    /// Audit event types
    /// </summary>
    public enum AuditEventType
    {
        UserLogin,
        UserLogout,
        AccountCreated,
        AccountModified,
        AccountDeleted,
        TransactionInitiated,
        TransactionCompleted,
        TransactionFailed,
        PermissionGranted,
        PermissionRevoked,
        DataExport,
        DataImport,
        ConfigurationChange
    }

    /// <summary>
    /// Transaction event types
    /// </summary>
    public enum TransactionEventType
    {
        Initiated,
        Validated,
        Authorized,
        Processing,
        Completed,
        Failed,
        Reversed,
        Refunded
    }
}
