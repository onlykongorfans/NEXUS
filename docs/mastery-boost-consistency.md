# Mastery boost consistency

Applied mastery boosts are recorded on `stat.MatchParticipantStatistics` using `MasteryBoostExperience` (nullable integer) and `MasteryBoostIsSuperBoost` (boolean). A non-null experience value identifies an applied boost, including a boost that grants zero experience.

The boost record, mastery experience, level rewards and consumable deduction commit together under the owning user's inventory lock. Duplicate requests check the durable record while holding that same lock. Redis publication happens only after the transaction succeeds; publication failure is logged without changing the successful result. Match statistics prefer the durable record, so a missing cache entry cannot hide a newly committed boost or allow it to be applied twice.

## Deployment

Migration `20260907003441_PersistAppliedMasteryBoost` adds the two fields without deleting or rewriting existing statistics. The production database initializer applies it before dependent services start. Deploy the migration with the updated master server. Older binaries can ignore the additional fields, but reverting to them also restores cache-only duplicate detection; do not drop the new fields as a routine rollback step.

Pre-existing boosts may exist only in Redis. Those entries remain a compatibility fallback and are copied into the durable fields when a repeated boost request encounters them, without applying experience or consuming another boost. Retain existing Redis data during deployment. Historical cache-only boosts that were already lost or inconsistent cannot be reconstructed reliably by this change.

Migration `20261010045455_AddRecordedMasteryProgressionAndHighestMedal` adds nullable per-match `MasteryProgression` JSON and a seasonal `HighestMedal`. Existing boost fields remain in place. The highest medal is initialised from the existing rating once placements are complete; historical season peaks before this change cannot be recovered. Existing match progression is not rewritten. Old matches continue to use the legacy percentage-based bonus calculation and durable/Redis boost records; boosting an eligible legacy match records its reconstructed progression alongside the existing boost marker.

New matches record their starting hero experience, match experience, number of maxed heroes, bonus and boost experience. Their bonus is one experience per maxed hero for ranked matches (halved and rounded up for casual and Mid Wars), capped at half the match experience. A regular boost adds the match and bonus experience once, for twice the original reward in total. Hero experience is capped at 36,100, while the client-facing per-match total remains uncapped so the client can correctly reconstruct the starting experience. Historical rewards and existing boost deductions are not recalculated.

Statistics and boost requests share the same eligibility checks. The fork's latest-match requirement, application window, inventory lock, atomic commit and post-commit cache publication remain in force. Do not drop either migration's new columns during a binary rollback.

## Verification

Mastery integration tests cover failed and cancelled commits, post-commit cache failures, overlapping requests while cache publication is delayed, reporting without a cache entry, regular and super boosts, and legacy cached boost records.

Additional regression tests cover boosting unboosted legacy matches, reading historical cached boosts without rewriting their records, recorded progression after subsequent hero progress, mastery caps, reward eligibility, and upgrading seasonal medals from the previous production schema.
