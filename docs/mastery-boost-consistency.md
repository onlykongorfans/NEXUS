# Mastery boost consistency

Applied mastery boosts are recorded on `stat.MatchParticipantStatistics` using `MasteryBoostExperience` (nullable integer) and `MasteryBoostIsSuperBoost` (boolean). A non-null experience value identifies an applied boost, including a boost that grants zero experience.

The boost record, mastery experience, level rewards and consumable deduction commit together under the owning user's inventory lock. Duplicate requests check the durable record while holding that same lock. Redis publication happens only after the transaction succeeds; publication failure is logged without changing the successful result. Match statistics prefer the durable record, so a missing cache entry cannot hide a newly committed boost or allow it to be applied twice.

## Deployment

Migration `20260907003441_PersistAppliedMasteryBoost` adds the two fields without deleting or rewriting existing statistics. The production database initializer applies it before dependent services start. Deploy the migration with the updated master server. Older binaries can ignore the additional fields, but reverting to them also restores cache-only duplicate detection; do not drop the new fields as a routine rollback step.

Pre-existing boosts may exist only in Redis. Those entries remain a compatibility fallback and are copied into the durable fields when a repeated boost request encounters them, without applying experience or consuming another boost. Retain existing Redis data during deployment. Historical cache-only boosts that were already lost or inconsistent cannot be reconstructed reliably by this change.

## Verification

Mastery integration tests cover failed and cancelled commits, post-commit cache failures, overlapping requests while cache publication is delayed, reporting without a cache entry, regular and super boosts, and legacy cached boost records.
