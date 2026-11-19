// The integration tests share process-wide environment variables with the real
// API host and use real PostgreSQL provisioning. Run them sequentially.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
