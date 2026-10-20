using System.Data;
using Microsoft.Data.SqlClient;
using Rakit.Core.Data;

namespace Rakit.Core.Workflows;

public interface IWorkflowRepository
{
    /// <summary>Ordered by name.</summary>
    Task<IReadOnlyList<WorkflowSummary>> ListAsync(CancellationToken ct = default);

    Task<Workflow?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>Creates a workflow in status Draft with its steps. Validates the input first.</summary>
    /// <exception cref="WorkflowValidationException"/>
    /// <exception cref="DuplicateWorkflowCodeException"/>
    Task<Workflow> CreateAsync(WorkflowInput input, string user, CancellationToken ct = default);

    /// <summary>Saves new content (the steps are replaced); the status stays as it is.</summary>
    /// <exception cref="WorkflowValidationException"/>
    /// <exception cref="DuplicateWorkflowCodeException"/>
    /// <exception cref="WorkflowNotFoundException"/>
    Task<Workflow> UpdateAsync(Guid id, WorkflowInput input, string user, CancellationToken ct = default);

    /// <summary>Publishing re-validates the stored content first.</summary>
    /// <exception cref="WorkflowValidationException"/>
    /// <exception cref="WorkflowNotFoundException"/>
    Task<Workflow> SetStatusAsync(Guid id, WorkflowStatus status, string user, CancellationToken ct = default);

    /// <summary>Duplicates the workflow as a new Draft with a free code ("{code}-copy", "{code}-copy2", ...) and "(salinan)" in the name.</summary>
    /// <exception cref="WorkflowNotFoundException"/>
    Task<Workflow> CopyAsync(Guid id, string user, CancellationToken ct = default);

    /// <summary>Soft delete. Refused while a (non-deleted) module still runs the workflow from one of its actions.</summary>
    /// <exception cref="WorkflowNotFoundException"/>
    /// <exception cref="WorkflowInUseException"/>
    Task DeleteAsync(Guid id, string user, CancellationToken ct = default);

    /// <summary>Appends tracking rows (one per step of a run).</summary>
    Task AddTrackingAsync(IReadOnlyList<TrackingEntry> entries, CancellationToken ct = default);

    /// <summary>The newest tracking rows of a workflow, newest first.</summary>
    Task<IReadOnlyList<TrackingEntry>> ListTrackingAsync(Guid workflowId, int take, CancellationToken ct = default);
}

public sealed class WorkflowRepository(IDbConnectionFactory connections) : IWorkflowRepository
{
    private const int DuplicateKeyError = 2601;
    private const int DuplicateKeyConstraintError = 2627;

    public async Task<IReadOnlyList<WorkflowSummary>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = await connections.CreateMetadataAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT w.Id, w.Code, w.Name, w.Status,
       (SELECT COUNT(*) FROM rakit.Steps s WHERE s.WorkflowId = w.Id AND s.IsDeleted = 0), w.Updated
FROM rakit.Workflows w
WHERE w.IsDeleted = 0
ORDER BY w.Name";

        var result = new List<WorkflowSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new WorkflowSummary(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2), WorkflowJson.StatusFromColumn(reader.GetString(3)),
                reader.GetInt32(4), reader.IsDBNull(5) ? null : reader.GetDateTime(5)));
        }
        return result;
    }

    public async Task<Workflow?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await connections.CreateMetadataAsync(ct);
        return await ReadOneAsync(conn, null, id, ct);
    }

    public async Task<Workflow> CreateAsync(WorkflowInput input, string user, CancellationToken ct = default)
    {
        EnsureValid(input);
        var id = Guid.NewGuid();

        await using var conn = await connections.CreateMetadataAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
INSERT INTO rakit.Workflows (Id, Code, Name, Description, Status, Property, CreatedBy, Created)
VALUES (@id, @code, @name, @description, N'draft', @property, @user, @now)";
                AddContentParameters(cmd, input);
                cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });
                cmd.Parameters.Add(new SqlParameter("@user", SqlDbType.NVarChar, -1) { Value = user ?? "" });
                cmd.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = DateTime.UtcNow });
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await InsertStepsAsync(conn, tx, id, input.Steps, user, ct);
            await tx.CommitAsync(ct);
        }
        catch (SqlException ex) when (ex.Number is DuplicateKeyError or DuplicateKeyConstraintError)
        {
            throw new DuplicateWorkflowCodeException(input.Code.Trim());
        }

        return (await ReadOneAsync(conn, null, id, ct))!;
    }

    public async Task<Workflow> UpdateAsync(Guid id, WorkflowInput input, string user, CancellationToken ct = default)
    {
        EnsureValid(input);

        await using var conn = await connections.CreateMetadataAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
UPDATE rakit.Workflows
SET Code = @code, Name = @name, Description = @description, Property = @property, UpdatedBy = @user, Updated = @now
WHERE Id = @id AND IsDeleted = 0";
                AddContentParameters(cmd, input);
                cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });
                cmd.Parameters.Add(new SqlParameter("@user", SqlDbType.NVarChar, -1) { Value = user ?? "" });
                cmd.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = DateTime.UtcNow });
                if (await cmd.ExecuteNonQueryAsync(ct) == 0) throw new WorkflowNotFoundException(id);
            }

            await using (var delete = conn.CreateCommand())
            {
                delete.Transaction = tx;
                // ParentId points at a sibling, so the rows go in one statement
                delete.CommandText = "DELETE FROM rakit.Steps WHERE WorkflowId = @id";
                delete.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });
                await delete.ExecuteNonQueryAsync(ct);
            }
            await InsertStepsAsync(conn, tx, id, input.Steps, user, ct);
            await tx.CommitAsync(ct);
        }
        catch (SqlException ex) when (ex.Number is DuplicateKeyError or DuplicateKeyConstraintError)
        {
            throw new DuplicateWorkflowCodeException(input.Code.Trim());
        }

        return (await ReadOneAsync(conn, null, id, ct))!;
    }

    public async Task<Workflow> SetStatusAsync(Guid id, WorkflowStatus status, string user, CancellationToken ct = default)
    {
        await using var conn = await connections.CreateMetadataAsync(ct);
        var current = await ReadOneAsync(conn, null, id, ct) ?? throw new WorkflowNotFoundException(id);

        if (status == WorkflowStatus.Published)
        {
            var errors = WorkflowInputValidator.Validate(ToInput(current));
            if (errors.Count > 0) throw new WorkflowValidationException(errors);
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE rakit.Workflows SET Status = @status, UpdatedBy = @user, Updated = @now WHERE Id = @id AND IsDeleted = 0";
        cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });
        cmd.Parameters.Add(new SqlParameter("@status", SqlDbType.NVarChar, 20) { Value = WorkflowJson.StatusToColumn(status) });
        cmd.Parameters.Add(new SqlParameter("@user", SqlDbType.NVarChar, -1) { Value = user ?? "" });
        cmd.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = DateTime.UtcNow });
        if (await cmd.ExecuteNonQueryAsync(ct) == 0) throw new WorkflowNotFoundException(id);

        return (await ReadOneAsync(conn, null, id, ct))!;
    }

    public async Task<Workflow> CopyAsync(Guid id, string user, CancellationToken ct = default)
    {
        await using var conn = await connections.CreateMetadataAsync(ct);
        var source = await ReadOneAsync(conn, null, id, ct) ?? throw new WorkflowNotFoundException(id);

        var copy = ToInput(source);
        const string nameSuffix = " (salinan)";
        copy.Name = source.Name.Length + nameSuffix.Length > WorkflowInputValidator.MaxNameLength
            ? source.Name[..(WorkflowInputValidator.MaxNameLength - nameSuffix.Length)] + nameSuffix
            : source.Name + nameSuffix;

        // a free code: "{code}-copy", then "-copy2", "-copy3", ... (the unique index decides; a clash just tries the next one)
        for (var attempt = 1; ; attempt++)
        {
            var suffix = attempt == 1 ? "-copy" : $"-copy{attempt}";
            copy.Code = source.Code.Length + suffix.Length > WorkflowInputValidator.MaxCodeLength
                ? source.Code[..(WorkflowInputValidator.MaxCodeLength - suffix.Length)] + suffix
                : source.Code + suffix;
            try
            {
                return await CreateAsync(copy, user, ct);
            }
            catch (DuplicateWorkflowCodeException) when (attempt < 50) { }
        }
    }

    public async Task DeleteAsync(Guid id, string user, CancellationToken ct = default)
    {
        await using var conn = await connections.CreateMetadataAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        await using (var count = conn.CreateCommand())
        {
            count.Transaction = tx;
            count.CommandText = @"
SELECT COUNT(DISTINCT m.Id)
FROM rakit.Modules m WITH (UPDLOCK, HOLDLOCK)
CROSS APPLY OPENJSON(m.Config, '$.actions') WITH (workflowId uniqueidentifier '$.workflowId') a
WHERE m.IsDeleted = 0 AND a.workflowId = @id";
            count.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });
            var modules = (int)(await count.ExecuteScalarAsync(ct))!;
            if (modules > 0) throw new WorkflowInUseException(id, modules);
        }

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE rakit.Workflows SET IsDeleted = 1, UpdatedBy = @user, Updated = @now WHERE Id = @id AND IsDeleted = 0";
        cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });
        cmd.Parameters.Add(new SqlParameter("@user", SqlDbType.NVarChar, -1) { Value = user ?? "" });
        cmd.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = DateTime.UtcNow });
        if (await cmd.ExecuteNonQueryAsync(ct) == 0) throw new WorkflowNotFoundException(id);

        await tx.CommitAsync(ct);
    }

    // ---- tracking ------------------------------------------------------------------------------

    private sealed class TrackingData
    {
        public string? StepCode { get; set; }
        public string? StepName { get; set; }
        public string? Status { get; set; }
        public string? Message { get; set; }
        public int DurationMs { get; set; }
        public Guid? ModuleId { get; set; }
        public string? ActionCode { get; set; }
        public string? RunBy { get; set; }
    }

    public async Task AddTrackingAsync(IReadOnlyList<TrackingEntry> entries, CancellationToken ct = default)
    {
        if (entries.Count == 0) return;
        await using var conn = await connections.CreateMetadataAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        foreach (var e in entries)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO rakit.TrackingSteps (Id, RunId, WorkflowId, Data, Created) VALUES (@id, @run, @workflow, @data, @created)";
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = e.Id });
            cmd.Parameters.Add(new SqlParameter("@run", SqlDbType.UniqueIdentifier) { Value = e.RunId });
            cmd.Parameters.Add(new SqlParameter("@workflow", SqlDbType.UniqueIdentifier) { Value = e.WorkflowId });
            cmd.Parameters.Add(new SqlParameter("@data", SqlDbType.NVarChar, -1)
            {
                Value = WorkflowJson.SerializeData(new TrackingData
                {
                    StepCode = e.StepCode, StepName = e.StepName, Status = WorkflowJson.TrackingToText(e.Status), Message = e.Message,
                    DurationMs = e.DurationMs, ModuleId = e.ModuleId, ActionCode = e.ActionCode, RunBy = e.RunBy,
                }),
            });
            cmd.Parameters.Add(new SqlParameter("@created", SqlDbType.DateTime2) { Value = e.Created });
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<TrackingEntry>> ListTrackingAsync(Guid workflowId, int take, CancellationToken ct = default)
    {
        await using var conn = await connections.CreateMetadataAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT TOP (@take) Id, RunId, WorkflowId, Data, Created
FROM rakit.TrackingSteps
WHERE WorkflowId = @id
ORDER BY Created DESC, Id";
        cmd.Parameters.Add(new SqlParameter("@take", SqlDbType.Int) { Value = Math.Clamp(take, 1, 500) });
        cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = workflowId });

        var result = new List<TrackingEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var data = WorkflowJson.DeserializeData<TrackingData>(reader.GetString(3)) ?? new TrackingData();
            result.Add(new TrackingEntry(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), data.StepCode ?? "", data.StepName,
                WorkflowJson.TrackingFromText(data.Status), data.Message, data.DurationMs, data.ModuleId, data.ActionCode,
                data.RunBy, reader.GetDateTime(4)));
        }
        return result;
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static void EnsureValid(WorkflowInput input)
    {
        var errors = WorkflowInputValidator.Validate(input);
        if (errors.Count > 0) throw new WorkflowValidationException(errors);
    }

    private static WorkflowInput ToInput(Workflow w) => new()
    {
        Code = w.Code, Name = w.Name, Description = w.Description, Transaction = w.Transaction,
        // round-trip through JSON so the copy never shares objects with the source
        Steps = w.Steps.Select(s => WorkflowJson.DeserializeStep(WorkflowJson.SerializeStep(s))).ToList(),
    };

    private static void AddContentParameters(SqlCommand cmd, WorkflowInput input)
    {
        cmd.Parameters.Add(new SqlParameter("@code", SqlDbType.NVarChar, 50) { Value = input.Code.Trim() });
        cmd.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 200) { Value = input.Name.Trim() });
        cmd.Parameters.Add(new SqlParameter("@description", SqlDbType.NVarChar, 500)
        {
            Value = string.IsNullOrWhiteSpace(input.Description) ? DBNull.Value : input.Description.Trim(),
        });
        cmd.Parameters.Add(new SqlParameter("@property", SqlDbType.NVarChar, -1)
        {
            Value = WorkflowJson.SerializeSettings(new WorkflowJson.Settings { Transaction = input.Transaction }),
        });
    }

    /// <summary>
    /// Inserts the steps in list order, then points each step's ParentId at the first step that links to it (a link may go
    /// to a step later in the list, so the rows exist before the parents are set).
    /// </summary>
    private static async Task InsertStepsAsync(
        SqlConnection conn, SqlTransaction tx, Guid workflowId, IReadOnlyList<WorkflowStep> steps, string? user, CancellationToken ct)
    {
        var normalized = steps.Select(Normalize).ToList();
        var ids = normalized.ToDictionary(s => s.Code, _ => Guid.NewGuid(), StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < normalized.Count; i++)
        {
            var step = normalized[i];
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
INSERT INTO rakit.Steps (Id, WorkflowId, ParentId, Data, CreatedBy, Created, [Order])
VALUES (@id, @workflow, NULL, @data, @user, @now, @order)";
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = ids[step.Code] });
            cmd.Parameters.Add(new SqlParameter("@workflow", SqlDbType.UniqueIdentifier) { Value = workflowId });
            cmd.Parameters.Add(new SqlParameter("@data", SqlDbType.NVarChar, -1) { Value = WorkflowJson.SerializeStep(step) });
            cmd.Parameters.Add(new SqlParameter("@user", SqlDbType.NVarChar, -1) { Value = user ?? "" });
            cmd.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = DateTime.UtcNow });
            cmd.Parameters.Add(new SqlParameter("@order", SqlDbType.Int) { Value = i });
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in normalized)
            foreach (var link in WorkflowGraph.LinksOf(step))
            {
                if (!ids.ContainsKey(link) || !assigned.Add(link)) continue;
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE rakit.Steps SET ParentId = @parent WHERE Id = @id";
                cmd.Parameters.Add(new SqlParameter("@parent", SqlDbType.UniqueIdentifier) { Value = ids[step.Code] });
                cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = ids[link] });
                await cmd.ExecuteNonQueryAsync(ct);
            }
    }

    /// <summary>The step as it is stored: trimmed, only the settings of its own type, links of a Condition in its branches only.</summary>
    internal static WorkflowStep Normalize(WorkflowStep step)
    {
        static string? Link(string? code) => string.IsNullOrWhiteSpace(code) ? null : code.Trim();
        static string? Text(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

        return new WorkflowStep
        {
            Code = step.Code.Trim(),
            Name = Text(step.Name),
            Type = step.Type,
            Next = step.Type == StepType.Condition ? null : Link(step.Next),
            X = step.X,
            Y = step.Y,
            DataSourceId = step.Type == StepType.DataSource ? step.DataSourceId : Guid.Empty,
            Rest = step.Type != StepType.Rest || step.Rest is null ? null : new RestStepConfig
            {
                Method = step.Rest.Method.Trim().ToUpperInvariant(),
                Url = step.Rest.Url.Trim(),
                Headers = step.Rest.Headers.Where(h => !string.IsNullOrWhiteSpace(h.Name)).Select(h => new NameValue { Name = h.Name.Trim(), Value = h.Value ?? "" }).ToList(),
                Body = string.IsNullOrEmpty(step.Rest.Body) ? null : step.Rest.Body,
                ContentType = Text(step.Rest.ContentType),
                TimeoutSeconds = step.Rest.TimeoutSeconds,
                Retries = step.Rest.Retries,
            },
            Email = step.Type != StepType.Email || step.Email is null ? null : new EmailStepConfig
            {
                To = step.Email.To.Trim(), Subject = step.Email.Subject.Trim(), Body = step.Email.Body, IsHtml = step.Email.IsHtml,
            },
            Page = step.Type != StepType.Page || step.Page is null ? null : new PageStepConfig { Html = step.Page.Html },
            Module = step.Type != StepType.Module || step.Module is null ? null : new ModuleStepConfig
            {
                ModuleId = step.Module.ModuleId,
                Parameters = step.Module.Parameters.Where(p => !string.IsNullOrWhiteSpace(p.Name)).Select(p => new NameValue { Name = p.Name.Trim(), Value = p.Value ?? "" }).ToList(),
            },
            Condition = step.Type != StepType.Condition || step.Condition is null ? null : new ConditionStepConfig
            {
                Left = step.Condition.Left.Trim(),
                Operator = step.Condition.Operator,
                Right = Text(step.Condition.Right),
                OnTrue = Link(step.Condition.OnTrue),
                OnFalse = Link(step.Condition.OnFalse),
            },
        };
    }

    private static async Task<Workflow?> ReadOneAsync(SqlConnection conn, SqlTransaction? tx, Guid id, CancellationToken ct)
    {
        string code, name;
        string? description;
        WorkflowStatus status;
        DateTime created;
        DateTime? updated;
        bool transaction;

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT Code, Name, Description, Status, Created, Updated, Property FROM rakit.Workflows WHERE Id = @id AND IsDeleted = 0";
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            code = reader.GetString(0);
            name = reader.GetString(1);
            description = reader.IsDBNull(2) ? null : reader.GetString(2);
            status = WorkflowJson.StatusFromColumn(reader.GetString(3));
            created = reader.GetDateTime(4);
            updated = reader.IsDBNull(5) ? null : reader.GetDateTime(5);
            transaction = WorkflowJson.DeserializeSettings(reader.IsDBNull(6) ? null : reader.GetString(6)).Transaction;
        }

        var steps = new List<WorkflowStep>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT Data FROM rakit.Steps WHERE WorkflowId = @id AND IsDeleted = 0 ORDER BY [Order]";
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) steps.Add(WorkflowJson.DeserializeStep(reader.GetString(0)));
        }

        return new Workflow(id, code, name, description, status, transaction, steps, created, updated);
    }
}
