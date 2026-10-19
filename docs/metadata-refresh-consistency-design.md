# Metadata Read and Refresh Consistency Design

Date: 2026-10-19, Asia/Shanghai.

Status: final design baseline with a local implementation; validation and remaining scope are tracked below. Upstream review is pending. This document specifies intended behavior; it does not claim that all behavior is implemented or accepted upstream.

This English edition is self-contained and intended for a future upstream submission. A separate Chinese edition supports local review.

## Goals and scope

Media information reads should use committed database values that remain valid for the current media source. Retrieval should occur only when information required by the current operation is unknown or invalid. Technical discovery, library scanning, and descriptive metadata import need explicit boundaries so that playback, subtitle maintenance, and background refreshes cannot unexpectedly overwrite curated fields.

The scope covers movies, series, seasons, episodes, and their alternate versions and multipart videos. Core rules apply to ordinary files, symbolic links, and STRM items. The fork's local metadata only import mode is a motivating example, not an upstream dependency. Other media types and plugins require corresponding tests before receiving the same guarantees.

A representative workflow imports NFO metadata without probing video files, translates and normalizes genres and production locations through the API, groups versions of the same movie, and then uses normal playback, scans, and maintenance. Curated results must remain effective within their explicitly protected scope.

Excluded from this work are a translation service, title-based automatic identity matching, a universal metadata provenance ledger, automatic rewriting of user NFO files, recovery after database deletion without a backup, media file writes, and redesign of the entire API.

## Current behavior and design boundaries

The inspected upstream baseline is `08c2ba6e0b6499122822f1c5b18bc426104538b4`. Local code references are pinned to fork commit `d49bd1bb15467d59f0a09240a629fe5abe255909` to distinguish fork extensions from upstream behavior. The following are observed behavior or upstream findings, rather than the proposed contract:

- `MediaSourceManager.GetPlaybackMediaSources` requests `FullRefresh` for STRM items or items missing their primary audio or video streams. Playback probing and descriptive refresh are not separated at this entry point. [Playback entry point](https://github.com/jellyfin/jellyfin/blob/08c2ba6e0b6499122822f1c5b18bc426104538b4/Emby.Server.Implementations/Library/MediaSourceManager.cs#L182-L194)
- Local metadata merging has an overwrite branch for locked NFO metadata. Production locations can also be appended during a non-replacing merge. Consequently, `ReplaceAllMetadata=false` does not mean that every existing value remains unchanged. [Merge branch](https://github.com/jellyfin/jellyfin/blob/08c2ba6e0b6499122822f1c5b18bc426104538b4/MediaBrowser.Providers/Manager/MetadataService.cs#L908-L917), [production locations](https://github.com/jellyfin/jellyfin/blob/08c2ba6e0b6499122822f1c5b18bc426104538b4/MediaBrowser.Providers/Manager/MetadataService.cs#L1295-L1304)
- Jellyfin already has field locks, `LockedFields`, and an item lock, `IsLocked`. Their NFO tags are `lockedfields` and `lockdata`. Field locks are a list, not a boolean prohibition on editing every field present in an NFO. [NFO documentation](https://jellyfin.org/docs/general/server/metadata/nfo/)
- The metadata editing API currently assigns genres and production locations directly. Locking does not prohibit all explicit API edits. [Editing entry point](https://github.com/orut34iop/jellyfin/blob/d49bd1bb15467d59f0a09240a629fe5abe255909/Jellyfin.Api/Controllers/ItemUpdateController.cs)
- In the inspected fork, `BaseItem.RefreshMetadata` handles owned items and images before reaching the metadata service, and video versions have recursive maintenance paths. Protection only at provider merge time is insufficient. [Refresh entry point](https://github.com/orut34iop/jellyfin/blob/d49bd1bb15467d59f0a09240a629fe5abe255909/MediaBrowser.Controller/Entities/BaseItem.cs), [version maintenance](https://github.com/orut34iop/jellyfin/blob/d49bd1bb15467d59f0a09240a629fe5abe255909/MediaBrowser.Controller/Entities/Video.cs)
- The fork's local metadata only mode limits video target access and remote retrieval; it does not prohibit subsequent NFO reads. Merging versions retains separate item records and does not normalize every member's genres or production locations. [Import policy](https://github.com/orut34iop/jellyfin/blob/d49bd1bb15467d59f0a09240a629fe5abe255909/MediaBrowser.Controller/Library/LocalMetadataOnlyImportPolicy.cs), [version merging](https://github.com/orut34iop/jellyfin/blob/d49bd1bb15467d59f0a09240a629fe5abe255909/Jellyfin.Api/Controllers/VideosController.cs)

These findings establish overwrite paths, but do not replace reproduction of the exact source of a particular item's changes. Implementation must begin with a minimal reproduction containing the API edit, NFO contents, lock state, and playback request.

### Reproduction protocol for implementation

This protocol is a proposed validation procedure, not a claim that a runtime reproduction has already been completed on the inspected upstream commit.

1. Record the server commit/version, client, enabled metadata providers and savers, library settings, and lock state. Use an isolated library with a small lawful test video and matching NFO containing distinctive original genre and country values. Disable unrelated scheduled work during the isolated playback test, then test those workflows separately.
2. Test an ordinary file and a STRM item separately. The inspected upstream STRM branch provides a refresh trigger without requiring the fork's import mode. For a missing-stream case, use a controlled fixture or supported import workflow and document how the missing state was produced; do not claim a standard file import naturally omits probing.
3. Through the metadata editing API, change genres and production locations to distinct curated values while preserving the endpoint's required DTO fields. Read back and save the values and locks. Disable NFO saving for the stale-NFO case and verify that the original source values remain in the NFO.
4. Request playback information and play the item. Capture the item and selected media-source identifiers, technical information before and after, refresh/provider logs, descriptive fields, locks, and version relationships. A failing baseline must demonstrate a committed value changing, rather than only a client cache displaying old text.
5. Repeat with explicit field locks, a protected empty location collection, and multiple versions. Exercise missing-data refresh, replace-all refresh, scans, and subtitle maintenance separately. Record which trigger reproduces each change; do not infer a common root cause from similar symptoms.
6. After implementation, run the same fixtures and the acceptance matrix below, including cold restart and concurrent API edits. Keep private media paths and credentials out of the upstream reproduction.

## Required behavior

| ID | Requirement |
| --- | --- |
| R1 | Ordinary reads and playback use current committed, valid database data without reimporting descriptive metadata as a side effect of the read. |
| R2 | An operation retrieves only information it requires and that is unknown or invalid. A missing overview does not justify a playback probe. |
| R3 | Providers running for technical discovery must not write genres, production locations, titles, overviews, or other descriptive fields, even if their results include those fields. |
| R4 | Automatic sources must not replace, append to, or clear protected descriptive fields. An explicitly protected empty value is valid. |
| R5 | A user or tool with editing permission may explicitly edit a protected field. Changing its value does not implicitly remove protection. |
| R6 | Ordinary scans and replace-all metadata refreshes respect field locks. Unlocking or resetting protected fields requires explicit intent. |
| R7 | An older refresh must not overwrite an edit, lock change, or version relationship committed after that refresh started. |
| R8 | Descriptive refresh and technical probing must not implicitly split, reconstruct, or reselect the primary of a manually established version group. |
| R9 | File access errors, network failures, and failed probes do not establish media deletion or invalidity of previously committed data. |

Current database data includes correctly synchronized or invalidated in-memory caches. It does not mean comparing a database timestamp with NFO modification time and allowing whichever is later to win.

Technical information includes duration, container, codecs, resolution, audio/video/subtitle streams, and chapters as required by the operation. Embedded video tags must not fill descriptive fields during technical discovery, even when those fields are empty. Descriptions, ratings, people, tags, and provider identifiers remain subject to their metadata or explicit identity-editing scope. Images use a separate refresh scope. Chapter, thumbnail, trickplay, or other technical maintenance jobs must obey these boundaries if they invoke shared refresh paths; this is a policy requirement, not a claim that every such job currently reimports NFO. These operations must not alter user playback history or favorites outside the ordinary, separately authorized playback-state update.

## Information states and retrieval

| State | Example | Behavior |
| --- | --- | --- |
| Known and valid | Codec for the current source; a genre saved through the API | Use the existing value. |
| Intentionally empty | A user clears production locations and protects the field | Return the empty value; do not treat it as missing. |
| Unknown | No video streams after import without probing | Retrieve when required by the current operation. |
| Confirmed invalid | Confirmed file replacement or changed STRM target | Invalidate relevant technical data and retrieve as needed; retain descriptive fields and group relationships. |
| Retrieval failed or temporarily unavailable | Offline NAS or probe timeout | Preserve committed data, return an appropriate error or usable result, and allow bounded retries. |

Empty, zero, and placeholder values require field-specific interpretation. Zero duration may be invalid while zero play count is valid. An NFO duration may suffice for display without proving that the current stream has the information required for transcoding. Validity depends on the field and its use.

First reuse field locks to express intentionally retained descriptive values, including empty values, together with existing source identity, file change evidence, and stream data. Propose minimal storage additions only where required states cannot otherwise be represented. A universal per-field provenance ledger is not a prerequisite.

Validity checks must not dereference symbolic links on every read. Synthetic timestamps or sizes are not evidence that the video changed. For remote sources without reliable change detection, retain a way to revalidate on demand, explicitly force a probe, or recover through bounded retries after specific playback errors. The presence of one stored stream must not prohibit revalidation forever.

Technical writes are scoped to a media source. A successful probe may atomically update related streams, duration, and container information for that source; it need not write only one missing scalar field. Validate new results before replacing related old results. Failure or cancellation must not first clear existing data.

## Field protection and editing

Reuse `LockedFields`, initially establishing complete coverage for `Genres` and `ProductionLocations` and verifying consistency for other supported descriptive fields affected by the work. Preserve the existing purpose of the item lock. Do not reinterpret NFO locking as an API permission restriction or infer editing prohibitions merely from XML tag presence.

A curation tool should save translated values and their protection state in the same logical update, preserving other existing locks. Intentionally retained empty values use the same contract. An API write alone does not prove that a field should permanently override its sources: callers must explicitly request protection. This avoids locking every unchanged field included in a full DTO submission.

For a single item, the server must persist the selected values and requested protection atomically, including protection against concurrent refresh commits. One HTTP request alone does not establish this guarantee. Verify the existing persistence path before relying on it. Across multiple items, expose progress and support resumable updates instead of implying a library-wide transaction. Imported NFO lock flags must not silently remove existing database protection or authorize overwriting protected values.

| Editing intent | Value and protection |
| --- | --- |
| Edit and protect genres | Save genres, add their field lock, and retain other locks. |
| Edit protected production locations again | Save the new value and retain protection. |
| Clear and protect production locations | Save an empty collection and retain protection. |
| No update submitted for a field | Do not infer a clear or unlock; verify the specific semantics of existing endpoints separately. |
| Explicitly unlock a field | Remove only the selected protection. |
| Ordinary full metadata refresh | Update unprotected fields while retaining protected fields. |
| Explicitly reset protected fields from a source | Operate on selected fields and items; commit only after successful retrieval without implicitly changing unrelated fields. |

The existing full DTO editing endpoint must not be treated as a PATCH endpoint. Integrations must follow its current contract and retain required fields and existing locks. This document does not provide executable sparse DTO examples. A playback bug fix must not silently redefine every client's editing behavior.

Resetting protected fields is a separate editing intent. An asynchronous sequence of unlock, queue refresh, and relock is not an atomic reset. If current APIs cannot express this safely, discuss an additive endpoint or explicit option separately. A successful reset retains the prior protection by default; unlocking is a separate operation. Retrieval failure retains both old values and old locks.

Technical validity and descriptive field locks are separate concerns. A probe may discover actual runtime information, but cannot use technical discovery to populate protected descriptive fields. Verify existing interactions between probing and lock enum members such as `Runtime` separately; this design does not broadly redefine all technical field locks.

## Refresh scenarios

| Scenario | Permitted retrieval and updates | Required preservation |
| --- | --- | --- |
| First import of a new item | Import NFO, images, and enabled sources according to library configuration; preserve ordinary probing rules for ordinary libraries | Do not misclassify an existing matched item as new and discard its protection. |
| First import in local metadata only mode | Parse local NFO and permitted local assets without accessing the video target | Do not probe targets or present placeholders as verified technical information. |
| Ordinary item details and lists | Read the database; assess existing entity-specific on-demand refreshes separately | Do not implicitly reimport movie or episode descriptions. |
| First playback with insufficient streams | Retrieve required technical information for the current source | All existing descriptive values, locks, and version groups. |
| Repeated playback with valid data | Use existing stream information | No repeated full refresh or probe solely because the extension is STRM. |
| Changed source or invalid technical data | Revalidate and probe the affected source | Curated descriptions and valid information for other versions. |
| Subtitle addition, download, upload, or deletion | Update subtitle discovery and related stream data using current directory contents | Descriptive fields; stale caches must neither restore deleted subtitles nor drop added subtitles. |
| Explicit image-only refresh | Discover or replace images within the requested image scope | Descriptive values, locks, stream data, and version relationships; image refresh is not permission to reimport descriptions. |
| Manual or scheduled library scan | Discover new items, check changes, and refresh unprotected fields under existing configuration | Protected values, including empty values, and existing version relationships. |
| File change monitoring | Determine the change type and perform necessary updates; enforce protection even if the event cannot be narrowed | Subtitle or image changes must not overwrite protected fields. |
| Automatic metadata refresh becomes due | Refresh permitted sources and unprotected fields | Field locks; technical probing must not count as a successful descriptive refresh. |
| Search for missing metadata | Fill genuinely unknown descriptive fields under source rules | Known values and protected empty values; missing-data retrieval must not append old production locations. |
| Replace all metadata | Replace unprotected fields and refresh images as configured | Field locks and manual version relationships. |
| Identify or change series display order | Perform the explicitly requested identity or ordering change and necessary recursive refresh | Protected fields; an identity change does not implicitly authorize splitting groups or unlocking. |
| Owned videos, multipart videos, and parent inheritance | Maintain relevant information only when required and check destination field locks | Independently protected child values; technical requests must not expand into unrelated descriptive refresh. |
| Merge, split, or select a primary version | Change relationships through explicit version management operations | Unselected descriptive fields, user playback data, and each source's technical information. |
| Plugin requests a standard refresh | Apply the same refresh scope and field protection | No bypass of the shared policy. Plugins writing directly to the database are outside this guarantee. |
| Reimport after deletion or rebuild the database | Explicitly restore from backups or a curation manifest | Do not promise automatic reconstruction of deleted locks and manual relationships. |

The fork already skips some background work, including automatic subtitle downloads, in local metadata only mode. This is a local compatibility concern; upstream behavior must not depend on those tasks being disabled. Person refreshes, genre entity validation, and other aggregate maintenance must be assessed by affected entity, rather than assumed to overwrite movie fields.

## Alternate versions and multipart videos

A version group shares explicit relationships. Each media source retains its own codec, duration, audio tracks, subtitles, and path. Probing one version must not overwrite another version or implicitly change a manually selected primary merely because a higher resolution was discovered.

Group members retain independent descriptive fields. The server does not automatically synchronize every member's title, overview, or production locations. A tool may apply selected common values and locks to an explicit member list after confirming identity. This is a curation operation, not an implicit scan behavior.

Resolve conflicts before merging. Different genres or production locations must not be resolved by arbitrary last-writer selection, automatic union, or matching titles alone. The tool determines final values, updates selected members and protection, and then establishes or confirms relationships. Interrupted operations must expose partial completion and support idempotent retry; multiple existing HTTP requests are not assumed to be one database transaction.

A new member follows normal import rules. It must not overwrite existing curation or silently inherit locks. Including it in group curation is explicit. Splitting a group preserves each member's values and locks. A temporarily inaccessible member does not justify splitting the group. Confirmed deletion still follows deletion rules; protection must not retain explicitly deleted records indefinitely.

Scanner-discovered versions, API-merged versions, and multipart videos require separate validation because their maintenance paths differ. A passing test for one does not establish correctness for the others.

## Implementation constraints and concurrency

Shared policy must cover refresh entry points, provider writes, parent inheritance, persistence, and cache invalidation. This design does not prescribe new public API names or concrete internal types.

The suggested sequence is to read the current item and target source, determine required information and its validity, choose the minimum retrieval scope, obtain results in temporary state, check whether values, locks, or source identity changed before committing, write only fields allowed by the operation, and invalidate or synchronize the affected caches.

Long network or ffprobe work should not hold a database write transaction. Existing item coordination or optimistic revision checks may implement concurrency control, but must demonstrate that an earlier refresh cannot overwrite a later edit, an old source result cannot be committed to a new source, and duplicate requests cannot cause unbounded duplicate probing. On conflict, reread and reassess or discard stale results.

Playback probing must not advance the timestamp meaning that descriptive metadata was refreshed. Use suitable independent state if probe freshness must be persisted. Failed probes remain retryable without retrying indefinitely on every request. Logging should distinguish reads, probes, descriptive refreshes, skipped protected writes, and explicit resets without recording tokens or credential-bearing remote URLs.

Initial implementation should reuse storage and field locks. If review establishes that essential technical state cannot be represented reliably, add a minimal backward-compatible migration separately rather than predefining a new general metadata database.

## Existing libraries and recovery

Before enabling protection, back up the database and manual version relationships and export the current values to protect. Neither Chinese text nor a nonempty value proves that a field was curated. A tool's processing record or an explicitly selected user scope determines the protected items.

Add locks to correctly curated existing values without rereading NFO files to initialize those values. Already reverted data requires a backup, curation manifest, or another translation pass; the fix cannot infer overwritten values. Subsequent tool updates save values and locks for every explicitly selected version member.

NFO writing remains governed by existing library configuration. This fix neither enables NFO saving by default nor changes the fork's local metadata only write policy. If saving is enabled, output should reflect committed protected values. Report write failures without replacing successful database edits with old NFO values.

Recovery after recreating items cannot rely solely on old ItemIds, titles, or one external provider ID. Matching requires sufficient evidence such as library scope, logical path, media type, and version membership. Ambiguous matches require human resolution.

## Acceptance matrix

| ID | Scenario | Expected result |
| --- | --- | --- |
| T01 | Repeated PlaybackInfo requests for ordinary files and STRM items with valid primary streams | No unnecessary full refresh or repeated probe; descriptions unchanged. |
| T02 | NFO import without streams, API curation, then playback | Required technical information is obtained without reverting curation; also test unlocked descriptive fields for playback isolation. |
| T03 | Edit and protect genres and locations, then scan and replace all metadata | Values and locks are unchanged. |
| T04 | Explicitly clear and protect production locations | Playback, missing-data retrieval, NFO changes, and full refresh cannot refill them. |
| T05 | NFO contains conflicting old values and lockdata or lockedfields | Operation scope and destination field locks prevail; source locks cannot overwrite protected database values. |
| T06 | Explicitly edit protected values, then separately unlock one field | Editing succeeds and preserves locks; only the selected lock is removed. |
| T07 | Reset selected protected fields with successful or failed retrieval | Success updates only the selected scope and retains protection; failure leaves values and locks intact. |
| T08 | Discover new movies and episodes and refresh unprotected source changes | Normal import and refresh continue; the library is not frozen. |
| T09 | Add, delete, and download subtitles with existing directory caches | Subtitle discovery is correct and descriptions unchanged; verify delivery endpoints when applicable. |
| T10 | Import and read ordinary libraries and local metadata only symlink libraries | Ordinary compatibility is preserved; local-only import and unnecessary reads do not access video targets. |
| T11 | Confirm video replacement or a changed STRM target | Reacquire only relevant technical data while preserving curated descriptions. |
| T12 | Probe timeout, cancellation, remote outage, and inaccessible directory | No data erasure, mistaken deletion, or group split; bounded recovery is possible. |
| T13 | Refresh manual groups, scanner-discovered versions, and multipart videos separately | Locks and relationships remain correct; technical data does not cross sources. |
| T14 | Change primary, split a group, and add a member | Existing values and locks remain; new members do not implicitly overwrite or inherit protection. |
| T15 | API commits values or locks during refresh; source changes concurrently | Old tasks cannot overwrite new commits or write to the wrong source. |
| T16 | Identify, change series ordering, inherit from parents, and refresh recursively | Protection remains effective and unrequested version relationships remain unchanged. |
| T17 | Restart, cold caches, and reload from the database | Values, locks, and relationships persist; descriptive refresh and probe freshness remain distinct. |
| T18 | Existing full DTO clients, external tools, and standard plugin refreshes | API compatibility without accidental clears or whole-item automatic locking. |
| T19 | NFO saver succeeds or fails when enabled | Correct values are written; failures are observable without reverting to old source values. |
| T20 | Restore backups, map reimported items, and resume interrupted curation | Recovery or explicit conflict reporting; no promise of recovery without a backup. |
| T21 | Request image-only refresh with missing images and conflicting NFO values | Images update as requested; descriptive fields and version relationships remain unchanged. |
| T22 | Save values and locks while a refresh is committing; interrupt a multi-item batch | Per-item writes are atomic; partial batch completion is reported and safely resumable. |

Use unit, API integration, and real-media tests. An upstream reproduction must work on an official branch with a normal test library, without the fork's fast-import setting, private media, or a private player. Assert retrieval counts, before/after API values, locks, group relationships, and values after cold start. Tests for an earlier partial patch do not replace this matrix.

## Upstream submission scope

| Unit | Included | Excluded |
| --- | --- | --- |
| A Playback information retrieval | Database-first reads, required technical discovery, isolation of descriptive writes, and tests covering hooks before the metadata service | Translation, bulk-management UI, and a complete metadata-source redesign. |
| B Field protection consistency | Reproducible lock bypasses in NFO merging, providers, and inheritance, with empty-value and relevant concurrency tests | Making locks prohibit explicit editing or locking every API field automatically. |
| C Subtitle maintenance | Necessary narrowing of refresh scope, directory freshness, and preservation of external subtitles | Treating pending third-party patches as merged fixes or adding unrelated playback optimizations. |
| D Editing and version operations | Separate proposals only where existing APIs cannot safely express protection, reset, or group operations | Silent changes to old endpoint semantics or default synchronization of every member field. |

Each submission must explain and validate its own behavior. A may fix playback overwrites first, but must not be described as protecting unlocked fields against explicit metadata refreshes. B first repairs the existing protection contract; new reset endpoints and group conveniences need separate maintainer agreement. Concurrency and cache correctness are acceptance conditions for affected submissions, not indefinite follow-up work.

Local fast-import compatibility, marking existing curated values, translation, and bulk-tool integration belong in the fork. Upstream changes must not include local paths, signing, installation scripts, private library configuration, or unrelated features. Any earlier partial implementation must be reassessed against the affected requirements before submission.

Submit the English document with future upstream work. Contributors must review the implementation and reproduction results and write their own issue descriptions, PR bodies, and responses under upstream policy, rather than copying generated communication. This design is a technical proposal for review, not a substitute for a contributor's explanation. [Contribution policy](https://jellyfin.org/docs/general/contributing/llm-policies/)

Human review and editing are required before submission; placing generated text in an attachment must not be used to bypass the communication policy. Acceptance of the document and final design remains at maintainers' discretion. Preserve authorship and license attribution if incorporating another contributor's patch.

## Related upstream work

Statuses were checked on 2026-10-19. A merged PR does not establish inclusion in a particular released version.

| Record | Status and relevance |
| --- | --- |
| [Issue 10088](https://github.com/jellyfin/jellyfin/issues/10088) and [PR 10090](https://github.com/jellyfin/jellyfin/pull/10090) | NFO merge parameter fix merged on 2023-08-11; it does not isolate playback from descriptive refresh. |
| [PR 12028](https://github.com/jellyfin/jellyfin/pull/12028) | Replace-all and metadata settings fix merged on 2024-06-15; not a complete fix for playback overwrites. |
| [Issue 12078](https://github.com/jellyfin/jellyfin/issues/12078) | Closed after inactivity; closure is not evidence of a fix. |
| [Issue 13655](https://github.com/jellyfin/jellyfin/issues/13655) | Closed as fixed on 2026-09-22, with another user report on October 16; concerns NFO edits and scans rather than this exact playback scenario. |
| [Issue 15882](https://github.com/jellyfin/jellyfin/issues/15882) and [Issue 10338](https://github.com/jellyfin/jellyfin/issues/10338) | The former is open; the latter was closed as duplicate. Both concern external subtitle loss on STRM playback. |
| [PR 17050](https://github.com/jellyfin/jellyfin/pull/17050) | Open and unmerged. Avoids some STRM probes with existing streams, but still uses full refresh when streams are missing. |
| [PR 17790](https://github.com/jellyfin/jellyfin/pull/17790) | Open and unmerged. Uses current directory contents for subtitle discovery, without directly protecting genres or locations. |
| [PR 16869](https://github.com/jellyfin/jellyfin/pull/16869) | Open and unmerged. Preserves external subtitles around probing, without addressing descriptive field overwrites. |
| [Issue 18031](https://github.com/jellyfin/jellyfin/issues/18031) | Open. Reports titles changing after subtitle operations; a shared root cause has not been established. |

## Completion criteria

Delivery requires all of the following: reads do not cause unrelated descriptive writes; required technical information remains retrievable; protected values and empty values survive refreshes; explicit editing remains usable; alternate versions and multipart sources do not contaminate one another; concurrency and failures do not restore old values; and normal new-content import continues to work.

The acceptance matrix defines the verification scope. Claim resolution only after the relevant implementation and validation are complete. Completing this document or merging part of the proposed work does not mean the implementation is complete.

## Implementation tracking

Updated 2026-10-19. The local implementation covers playback isolation, existing field-lock enforcement, subtitle maintenance, and technical persistence. The validation evidence below establishes the tested scope, not completion of the entire acceptance matrix.

| Area | Current implementation work | Verification status |
| --- | --- | --- |
| Playback | Skip owned-item maintenance during technical retrieval; refresh the item belonging to each missing media source; reuse persisted primary streams, including STRM | Automated regression tests pass; see validation evidence below. |
| Subtitle maintenance | Route upload, download, deletion, and scheduled download completion to external-stream discovery; avoid full video probing, embedded tags, attachment replacement, and chapter replacement | Automated regression tests pass; see validation evidence below. |
| Field protection | Respect destination locks during owned-item inheritance; missing-data merges no longer append old studios, tags, or locations or let an NFO lock imply replacement | Automated regression tests pass; see validation evidence below. |
| Alternate versions | Remove the fork's unconditional copying of primary descriptive fields from `Video.UpdateToRepositoryAsync`; members keep independent values on save | Automated regression tests pass; see validation evidence below. |
| Image-only refresh | Skip descriptive refresh hooks and do not advance descriptive refresh completion time | Automated regression tests pass; see validation evidence below. |
| Concurrent edits | Coordinate same-item API edits and metadata service execution with an asynchronous keyed lock; recheck missing streams after acquiring it | Serialized edit/refresh and stale-data persistence tests pass; latest destination locks are reread after acquiring the lock. |
| Technical persistence | `ItemUpdateType.MediaInfo` writes only technical columns and an explicit allowlist within the legacy JSON payload; it bypasses NFO saving and version relationship reconciliation | SQLite tests cover stale descriptive values, retained JSON relationship data, and deleted or moved items. |
| Confirmed source changes | A changed STRM target discovered by normal refresh invalidates old embedded stream information; an obsolete or empty probe result is rejected | Invalid/stale result tests and isolated changed-STRM validation pass. |
| Explicit revalidation | Add `refreshMediaInfoOnly=true` to the existing item refresh endpoint to force a technical probe, including remote targets, without importing descriptions | API validation and an isolated forced reprobe from 160×90 to 320×180 pass. |

For explicit revalidation, an authorized administrator can call `POST /Items/{itemId}/Refresh?refreshMediaInfoOnly=true`. This queues work for a video or audio item; HTTP 204 confirms queuing, not successful completion. Conflicting image/trickplay or metadata replacement options return HTTP 400. The option defaults to false, so existing requests retain their API contract. It is independent of a reset of protected descriptive fields and does not unlock anything.

Normal playback does not inspect a symlink target or STRM file on every read to determine freshness. Source changes are discovered through the existing scan/monitor refresh path, or an explicit revalidation request. A `FileExists` failure during video refresh no longer deletes an owned video or trims its local-version list; deletion remains the responsibility of successful library reconciliation or explicit deletion.

The technical persistence path preserves committed descriptive fields and version data even when passed a stale item instance. Streams, attachments, and chapters still use their existing repositories: this is not yet a single transaction spanning all technical tables. The implementation rejects invalid/obsolete probe results before applying them, retains attachments and chapters during subtitle-only maintenance, and does not claim crash atomicity across all repositories.

After technical persistence, cached item and parent state is invalidated and the existing item-updated event is emitted. Refreshes and owned-item inheritance reread the current item after acquiring its lock; folder lock propagation also rereads each destination.

The keyed lock is process-local coordination, not a database transaction held during network access. It also covers owned-item inheritance writes and existing folder lock propagation, but does not authorize claims about plugins that bypass standard entry points. Explicit protected-field reset, comprehensive failure atomicity, library recovery, and the full acceptance matrix still require their own implementation or verification; they must not be inferred from the playback fix.

## Validation and review record

Validation date: 2026-10-19. Tests use the local implementation and generated two-second videos; they do not establish that an official upstream release already contains the fix.

| Check | Result |
| --- | --- |
| `Jellyfin.Providers.Tests` | 531 passed. |
| `Jellyfin.Controller.Tests` | 223 passed. |
| `Jellyfin.Server.Implementations.Tests` | 922 passed, 13 platform-specific tests skipped on macOS. |
| `Jellyfin.Api.Tests` | 177 passed. |
| Server Release build | Succeeded with zero warnings and errors. |
| Native launcher and network configuration regression | 13 launcher checks and 9 network migration tests passed. |

For each of the four test projects, build the Release configuration, then run `dotnet test tests/<project>/<project>.csproj --configuration Release --no-build --no-restore`. Every local build used the same `JellyfinBuildDateTime` timestamp. On this macOS host, test executables were explicitly signed with its valid development certificate before execution. Tests ran with `LANG=en_US.UTF-8 LC_ALL=en_US.UTF-8`: two pre-existing localization assertions expect English and fail under the host's Chinese default culture. This is separate from metadata refresh behavior.

An isolated server and database passed eight real-media checks: ordinary STRM and fast symlink NFO import; first/repeated playback preserving unlocked API edits; API merging with protected empty locations; replace-all refresh respecting locks; subtitle upload/deletion; forced technical revalidation from 160×90 to 320×180; cold restart retaining values, locks, version groups and cached streams while remote targets were offline; and ordinary refresh detecting a changed STRM target followed by playback probing its new resolution. The tests used public HTTP endpoints and read values back from the database-facing API. They did not play through a graphical client, validate all transcoding profiles, or exercise live third-party subtitle downloads.

Two iterative reviews accompanied implementation. The first traced refresh entry points, inheritance, persistence and cache handling against R1–R9; it identified the need for technical-only persistence and rereading destination locks after item coordination. The second compared test evidence and both documents against T01–T22, corrected test fixtures for refreshed item lookup, and kept unverified claims out of the completion status. These were implementation self-reviews, not independent maintainer approval.

The eight checks and automated tests cover the reported movie workflow; they do not certify every subcase in T01–T22. Remaining work includes comprehensive episode/series recursion, multipart and group mutation concurrency, atomic protected-field reset, external provider failure combinations, enabled NFO-saver failure handling, recovery manifests, and crash atomicity across technical repositories. Existing full DTO requests also cannot prevent a caller from submitting an already stale snapshot without an API revision contract. These limits must accompany any upstream submission.

The correct external toolkit is Emby115Toolkit; the initially supplied nas-ai-tools checkout was unrelated to these API operations. Integration is now implemented in `MediaServerClient._save_curated_field`: each actual genre or location translation/deduplication saves its value and corresponding lock using the current full DTO, preserves all existing locks and `LockData`, and verifies the value and every submitted lock by reading the item back. Unconfirmed protected writes are reported as failures without replaying a stale full DTO. Emby retains its existing behavior. Existing unchanged Chinese values and empty values are not automatically locked; only deliberate cleanup that produces an empty result gains protection. Historical backfill still requires explicit item selection and backup. No real library values or locks were batch changed.

Tool integration was exercised against the isolated local server with three generated test movies: both translation workflows, version merge, replace-all refresh, playback, and a protected empty location value passed. Movie and Series workflows also run against both existing OpenAPI contract fixtures; Series was not tested on a live server. Version merging remains a relationship operation and does not synchronize metadata. Curate every intended member before merging; current list enumeration may hide already merged alternate versions, which need explicit member-ID handling. The tool does not introduce an atomic library-wide transaction or a revision-checked full DTO API.

Installed local version `12.0.0-20261019183458` passed development-certificate verification, HTTP 200, native menu diagnostics, launcher/server parent-child checks, graceful shutdown and relaunch. The login preference was preserved. This validates local delivery, not upstream acceptance.

Toolkit validation on 2026-10-19: 361 tests passed, one Windows-only test was skipped on macOS, and scoped Ruff checks passed. Shared genre normalization now drops blank entries, matching country cleanup and preventing a server-normalized empty value from being misreported as a failed save. The Python environment emitted its existing urllib3/LibreSSL compatibility warning.
