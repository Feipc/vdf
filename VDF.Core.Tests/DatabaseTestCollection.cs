namespace VDF.Core.Tests;

/// <summary>
/// Tests in this collection mutate DatabaseUtils' process-wide static database
/// and must not overlap with one another.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DatabaseTestCollection {
	public const string Name = "Database state";
}
