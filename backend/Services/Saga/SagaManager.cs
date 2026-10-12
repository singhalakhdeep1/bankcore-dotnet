using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BankingAPI.Data;

namespace BankCore.Services.Saga
{
    /// <summary>
    /// Saga Pattern Implementation for Transaction Workflows
    /// Manages distributed transactions with compensation logic
    /// </summary>
    public class SagaManager
    {
        private readonly Dictionary<string, SagaExecution> _executions = new();
        private readonly ILogger<SagaManager> _logger;

        public SagaManager(ILogger<SagaManager> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Execute a saga with compensation steps
        /// </summary>
        public async Task<T> ExecuteAsync<T>(SagaDefinition<T> saga, T context)
        {
            var executionId = $"saga_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}";
            
            var execution = new SagaExecution<T>
            {
                Id = executionId,
                SagaName = saga.Name,
                Status = SagaStatus.Pending,
                Context = context,
                StartedAt = DateTime.UtcNow,
                CompletedSteps = new List<string>(),
                FailedSteps = new List<string>()
            };

            _executions[executionId] = execution;
            _logger.LogInformation($"Starting saga: {saga.Name} (ID: {executionId})");

            try
            {
                for (int i = 0; i < saga.Steps.Count; i++)
                {
                    var step = saga.Steps[i];
                    execution.CurrentStep = i;

                    _logger.LogInformation($"Executing step {i + 1}/{saga.Steps.Count}: {step.Name}");

                    try
                    {
                        var result = await step.Execute(context);
                        context = result;
                        execution.CompletedSteps.Add(step.Name);
                        
                        _logger.LogInformation($"Step completed: {step.Name}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Step failed: {step.Name}");
                        execution.Status = SagaStatus.Compensating;
                        execution.FailedSteps.Add(step.Name);
                        execution.Error = ex.Message;

                        // Compensate all completed steps in reverse order
                        await CompensateAsync(saga, context, execution, i);
                        
                        execution.Status = SagaStatus.Failed;
                        execution.CompletedAt = DateTime.UtcNow;
                        throw;
                    }
                }

                execution.Status = SagaStatus.Completed;
                execution.CompletedAt = DateTime.UtcNow;
                _logger.LogInformation($"Saga completed successfully: {saga.Name}");

                return context;
            }
            finally
            {
                // Clean up old executions
                if (_executions.Count > 100)
                {
                    var oldestKey = _executions.Keys.First();
                    _executions.Remove(oldestKey);
                }
            }
        }

        /// <summary>
        /// Compensate completed steps in reverse order
        /// </summary>
        private async Task CompensateAsync<T>(SagaDefinition<T> saga, T context, SagaExecution<T> execution, int failedStepIndex)
        {
            _logger.LogInformation("Starting compensation...");

            for (int i = failedStepIndex - 1; i >= 0; i--)
            {
                var step = saga.Steps[i];
                if (step.Compensate != null && execution.CompletedSteps.Contains(step.Name))
                {
                    _logger.LogInformation($"Compensating step: {step.Name}");
                    try
                    {
                        await step.Compensate(context);
                        _logger.LogInformation($"Compensation completed: {step.Name}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Compensation failed for step: {StepName}", step.Name);
                        // Continue with other compensations even if one fails
                    }
                }
            }
        }

        /// <summary>
        /// Get execution status
        /// </summary>
        public SagaExecution? GetExecution(string executionId)
        {
            return _executions.TryGetValue(executionId, out var execution) ? execution : null;
        }
    }

    /// <summary>
    /// Saga definition with steps
    /// </summary>
    public class SagaDefinition<T>
    {
        public string Name { get; set; } = string.Empty;
        public List<SagaStep<T>> Steps { get; set; } = new();
    }

    /// <summary>
    /// Individual saga step
    /// </summary>
    public class SagaStep<T>
    {
        public string Name { get; set; } = string.Empty;
        public Func<T, Task<T>> Execute { get; set; } = default!;
        public Func<T, Task>? Compensate { get; set; }
        public bool Retryable { get; set; } = true;
        public int MaxRetries { get; set; } = 3;
    }

    /// <summary>
    /// Saga execution tracking
    /// </summary>
    public class SagaExecution
    {
        public string Id { get; set; } = string.Empty;
        public string SagaName { get; set; } = string.Empty;
        public SagaStatus Status { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public int CurrentStep { get; set; }
        public string? Error { get; set; }
    }

    public class SagaExecution<T> : SagaExecution
    {
        public T Context { get; set; } = default!;
        public List<string> CompletedSteps { get; set; } = new();
        public List<string> FailedSteps { get; set; } = new();
    }

    public enum SagaStatus
    {
        Pending,
        InProgress,
        Completed,
        Failed,
        Compensating
    }
}
