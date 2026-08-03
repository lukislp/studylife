using StudyLife.Server.Services;

namespace StudyLife.Server.Tests;

/// <summary>
/// ICurrentUserAccessor for tests that construct a StudyLifeDb directly without a DI container
/// (DatabaseBackupServiceTests, StudyProgramCatalogTests, MultiTenantFoundationTests): returns
/// a fixed AuthUserId so that SaveChanges stamping and global query filters deterministically
/// point to the same user. Default 1 = the Id that the migration/stamping assigns in a
/// fresh test DB.
/// </summary>
internal sealed class TestCurrentUserAccessor : ICurrentUserAccessor
{
    public int AuthUserId { get; init; } = 1;
}
