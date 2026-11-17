# Fault regression matrix (IB-26)

November 17, 2026 · [POS-DEC-010](POS-DEC-010.md). Every fault named in the design maps to automated tests that run against **real SQLite and real files**, not mocks of the database. Tests live in `tests/Beachtro.Infrastructure.Tests`; class names are given as `Class.Method` (shortened).

Each fault is checked against two rules: it leaves **no partial financial record**, and the same request completes **exactly once** after the fault clears.

| Fault | What must hold | Tests |
|---|---|---|
| **Power loss / crash mid-sale** | Rolled back, or committed exactly once; the reserved number is skipped, never reused | `ConstraintTests.A_failed_write_inside_the_checkout_transaction_leaves_no_partial_sale`, `RecoveryTests.A_reserved_number_whose_sale_never_committed_is_skipped_not_reused`, `CheckoutTests.Completing_twice_returns_the_same_sale_and_writes_nothing_more`, `ReceiptAndHistoryTests.Interrupted_submissions_become_outcome_unknown_on_restart` |
| **Restart with a refund prepared** | Completes once after the restart | `FaultRegressionTests.A_refund_prepared_before_a_restart_completes_once_after_it`, `CorrectionTests.Repeating_a_completed_correction_returns_it_without_a_second_refund` |
| **Number journal torn, damaged, missing or unwritable** | Torn tail repaired with evidence; damaged or missing blocks the series; unwritable refuses the sale with nothing written | `RecoveryTests.A_torn_final_journal_line_is_repaired_with_evidence_preserved`, `RecoveryTests.A_damaged_journal_blocks_the_series_until_the_owner_proves_a_high_water_mark`, `RecoveryTests.A_missing_journal_blocks_existing_series`, `FaultRegressionTests.An_unwritable_number_journal_refuses_the_sale_and_writes_nothing` |
| **Database locked by another writer** | Clear "busy, nothing saved" result; the retry completes once | `FaultRegressionTests.A_database_held_busy_by_another_writer_leaves_no_partial_sale_and_the_retry_completes_once`, `RuntimeTests.Second_writer_for_same_data_directory_is_refused` |
| **Disk full** | Backup refused before writing; journal write refused; artifact writes retried later | `RecoveryTests.A_backup_is_refused_when_disk_space_is_short_and_nothing_is_written`, `FaultRegressionTests.An_unwritable_number_journal_refuses_the_sale_and_writes_nothing`, `FaultRegressionTests.An_unwritable_artifact_folder_never_blocks_selling_and_files_appear_once_it_clears` |
| **Crash during file creation** | Completed by hash after rename; temp files removed; a different file is never overwritten | `ArtifactTests.A_crash_after_rename_but_before_ready_is_completed_by_hash`, `ArtifactTests.Temp_files_left_by_a_crash_are_removed`, `ArtifactTests.A_different_file_at_the_path_is_held_and_never_overwritten`, `ArtifactTests.A_missing_file_is_regenerated_only_with_identical_bytes` |
| **Network outage / rate limit** | Queue persists, backs off, nothing fails or is lost; each file uploaded once afterwards | `FaultRegressionTests.Closing_the_day_during_an_upload_outage_keeps_every_file_and_uploads_each_once_later`, `SyncTests.A_transient_failure_backs_off_honours_retry_after_and_keeps_history`, `SyncTests.Backoff_uses_full_jitter_with_a_one_hour_cap_and_honours_retry_after` |
| **Lost acknowledgement / lease expiry** | No second remote file | `SyncTests.A_lost_acknowledgement_never_creates_a_second_file`, `SyncTests.A_worker_whose_lease_expired_cannot_acknowledge_or_create_a_duplicate` |
| **Authorization or permission lost (destination)** | Waits for a manual retry with the same identity | `SyncTests.An_authorization_failure_waits_for_a_manual_retry_that_keeps_the_same_identity` |
| **Remote content differs** | Held, never overwritten | `SyncTests.A_remote_file_with_other_content_under_our_id_is_held_not_overwritten`, `RecoveryDrillTests.A_held_upload_whose_destination_copy_differs_stays_held` |
| **Session locked, expired or ended mid-flow** | Lock and reauthentication enforced; an ended session cannot complete, and the request stays recoverable | `IdentityTests.Inactivity_locks_the_session_and_the_password_unlocks_it`, `IdentityTests.Sensitive_actions_need_reauthentication_within_five_minutes`, `AuthorizationTests.Role_and_activation_changes_end_the_target_sessions`, `FaultRegressionTests.A_session_ended_between_prepare_and_complete_cannot_complete_and_leaves_the_request_prepared` |
| **Concurrent corrections** | Over-refund impossible | `CorrectionTests.F11_two_prepared_refunds_for_the_last_unit_cannot_both_commit`, `CorrectionTests.A_void_reverses_everything_once_and_is_refused_after_a_partial_refund` |
| **Report changes after close** | Watermark kept; later changes visible as a new revision | `ReportTests.Business_close_records_a_watermark_and_shows_later_changes`, `ArtifactTests.A_business_day_close_stores_its_csv_and_pdf_from_the_report_run` |
| **Restore of an old or foreign backup** | Tampered, foreign or newer backups refused; old backups never reuse numbers or duplicate uploads | `RecoveryTests.A_tampered_backup_is_rejected`, `RecoveryTests.A_package_with_unexpected_members_is_rejected`, `RecoveryTests.Restoring_an_old_backup_preserves_evidence_pauses_selling_and_never_reuses_numbers`, `SyncTests.Restoring_an_older_database_reuses_journaled_identities_and_uploads_nothing_twice` |
| **Total device loss** | Replacement-PC restore; possibly-used series held; unknown uploads held then reconciled; unfinished payments closed, not replayed | `RecoveryDrillTests.An_old_backup_restored_on_a_new_pc_reuses_no_number_and_uploads_nothing_twice`, `RecoveryDrillTests.New_pc_restore_needs_an_account_that_may_restore` |
| **Schedule missed while closed** | Catch-up runs once per due key | `SchedulerTests.Missed_completed_months_are_caught_up_with_stored_files`, `SchedulerTests.The_daily_backup_runs_once_per_business_day` |
| **Direct database edits of history** | Guard triggers refuse them | `ConstraintTests.Financial_history_cannot_be_edited_or_deleted`, `SchemaTests.Every_guard_trigger_is_installed`, `ConstraintTests.Ef_update_of_a_completed_order_is_refused` |

## Fixed by this regression

- **Busy database:** a sale or refund finished while another program held the database threw a raw database error, which the cashier saw as "Something went wrong". It now returns "The database is busy … nothing was saved … try again". Any number set aside stays skipped.
- **Replacement PC:** a file not yet queued when the backup was taken would have been uploaded again under a new identity, although the lost PC may already have uploaded it. It is now held until the destination is searched (`UploadIdentityCutoffUtc`).
- **New business dates after a backup:** a date with no series in the backup (for example a day the lost PC traded after the backup) could have started again at 1. Every date from the backup to today is now blocked until proven.

## Not covered by automation

These need real hardware or accounts, which belong to IB-28:
- a real power cut of the physical PC;
- antivirus file locks;
- Windows disk-full on the system drive;
- real Drive quota and latency.
