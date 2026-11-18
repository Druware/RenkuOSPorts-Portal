using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RenkuPorts.Server.Data.Migrations
{
    /// <summary>
    /// Data only: the digest stopped hashing the packager (it names the building client), so undecided jobs are judged again.
    /// Their completed builds' stored digests and diffs are cleared, which makes <see cref="Services.BuildVerifier"/> recompute them,
    /// and jobs waiting on a decision go back to Verifying (the verifier polls for those every few seconds).
    /// </summary>
    public partial class RedigestWithoutPackager : Migration
    {
        /// <summary>
        /// Jobs whose completed builds still have every port package's blob; a job with any build missing one keeps its old digests
        /// entirely, so it is never judged on a mix of old- and new-rule digests (and the verifier never downloads a missing blob).
        /// Queued jobs with two completed builds and no build running were re-queued after a mismatch: Verifying either approves them
        /// or queues them again exactly as before (attempts and build numbers are not touched). Public for the test.
        /// </summary>
        public const string Sql = """
            DECLARE @jobs TABLE (Id bigint PRIMARY KEY);
            INSERT @jobs SELECT j.Id FROM Jobs j
            WHERE j.Status IN ('Verifying', 'AwaitingOverride', 'Queued', 'InProgress')
              AND NOT EXISTS (SELECT 1 FROM JobBuilds b JOIN StagedPackages p ON p.BuildId = b.Id
                              WHERE b.JobId = j.Id AND b.Status = 'Completed' AND p.Extra = 0 AND p.BlobName = '');

            UPDATE p SET Digest = NULL, ListingJson = NULL
            FROM StagedPackages p JOIN JobBuilds b ON b.Id = p.BuildId JOIN @jobs x ON x.Id = b.JobId
            WHERE b.Status = 'Completed' AND p.Extra = 0;

            UPDATE b SET Digest = NULL, DiffJson = NULL
            FROM JobBuilds b JOIN @jobs x ON x.Id = b.JobId
            WHERE b.Status = 'Completed';

            UPDATE j SET Status = 'Verifying'
            FROM Jobs j JOIN @jobs x ON x.Id = j.Id
            WHERE j.Status = 'AwaitingOverride'
               OR (j.Status = 'Queued'
                   AND (SELECT COUNT(*) FROM JobBuilds b WHERE b.JobId = j.Id AND b.Status = 'Completed') >= 2
                   AND NOT EXISTS (SELECT 1 FROM JobBuilds b WHERE b.JobId = j.Id AND b.Status = 'InProgress'));
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(Sql);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo: cleared digests are recomputed on demand, and the old rule cannot be restored from the data.
        }
    }
}
