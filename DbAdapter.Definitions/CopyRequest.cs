using CanonicalModel.DTOs;
using CanonicalModel.Models;

namespace DbAdapter.Definitions;

/// <summary>
/// Represents a request to copy a database: both endpoints as targets and the model that
/// has already been filtered by the application's table selection.
/// </summary>
/// <param name="Source">Endpoint the objects and rows are read from.</param>
/// <param name="Target">Endpoint the objects and rows are reproduced into.</param>
/// <param name="Model">Source model the session must reproduce, selection already applied.</param>
/// <param name="Options">Behaviour switches of the copy.</param>
public sealed record CopyRequest(
    DatabaseTarget Source,
    DatabaseTarget Target,
    DatabaseModel Model,
    CopyOptions Options);
