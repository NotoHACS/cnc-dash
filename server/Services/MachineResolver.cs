using CncDash.Server.Data;
using CncDash.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CncDash.Server.Services;

/// <summary>
/// Resolves a URL path segment to a machine. Short numeric URLs ("12") match
/// ids exactly, then by unique suffix ("cnc12"). Ambiguity is an error, never
/// a silent pick.
/// </summary>
public class MachineResolver
{
    public class ResolveResult
    {
        public Machine? Machine { get; init; }
        public bool NotFound { get; init; }
        public List<string>? AmbiguousCandidates { get; init; }
        public bool Resolved => Machine is not null;
    }

    private readonly IServiceScopeFactory scopes;

    public MachineResolver(IServiceScopeFactory scopes) => this.scopes = scopes;

    /// <summary>Null segment → null result (caller decides: picker / default).
    /// Order: exact id, then normalized ("cnc"-prefixed, "-") match, else unique
    /// suffix; multiple suffix matches → AmbiguousCandidates.</summary>
    public async Task<ResolveResult?> ResolveAsync(string? segment)
    {
        if (segment is null)
            return null; // caller decides (picker)

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CncDbContext>();

        var id = segment.Trim().ToLowerInvariant();

        var exact = await db.Machines.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id);
        if (exact is not null)
            return new ResolveResult { Machine = exact };

        var matches = await db.Machines.AsNoTracking()
            .Where(m => m.Id.EndsWith(id))
            .OrderBy(m => m.Id)
            .ToListAsync();

        var normalized = matches
            .Where(m => m.Id == "cnc" + id || m.Id.EndsWith("-" + id))
            .ToList();

        if (normalized.Count == 1)
            return new ResolveResult { Machine = normalized[0] };

        if (matches.Count == 1)
            return new ResolveResult { Machine = matches[0] };

        if (matches.Count > 1)
            return new ResolveResult { AmbiguousCandidates = matches.Select(m => m.Id).ToList() };

        return new ResolveResult { NotFound = true };
    }
}