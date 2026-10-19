namespace Rakit.Core.Schema;

/// <summary>The schema is not in AllowedSchemas.</summary>
public sealed class SchemaAccessDeniedException(string message) : Exception(message);

/// <summary>No such object, or an object that must never be exposed (system procedures).</summary>
public sealed class SchemaObjectNotFoundException(string message) : Exception(message);
