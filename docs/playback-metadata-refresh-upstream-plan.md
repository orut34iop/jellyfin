# Playback-triggered descriptive metadata changes: upstream priority plan

Verified on 2026-10-19. Official `master`: `08c2ba6e0b6499122822f1c5b18bc426104538b4`, reconfirmed against the remote in this review. Local implementation and validation baseline: `4416c74b72`.

Status: issue analysis, evidence and submission order, not a published PR body. The core fix has not been ported to an isolated upstream branch or reviewed by maintainers. This update changes documentation and priorities only, without changing code or the real library. [中文](playback-metadata-refresh-upstream-plan.zh-CN.md) · [Full design](metadata-refresh-consistency-design.md)

## 1. Actual workflow and symptom

Our routine workflow imports a media library from NFO files, then uses our external tool and the Jellyfin editing API to translate and normalize movie genres and production locations. The source NFO may still contain the original values.

Our symlink library uses a custom fast import mode: NFO metadata is read without probing the target videos for streams. After API editing, the library shows the curated values. Subsequently playing these movies causes the server to enter metadata refresh and curated values revert to old source information. The user did not request re-identification or replacement of descriptive metadata.

Fast import is the motivating local customization, not a proposed upstream dependency. The upstream reproduction should use its existing STRM path and a controlled fixture missing its primary stream to demonstrate the same official refresh entry point.

## 2. Trigger and cause

```text
NFO import: original genres / production locations
    ↓
API editing: database has curated values; NFO still has old values
    ↓
Player requests PlaybackInfo
    ↓
MediaInfoHelper → MediaSourceManager.GetPlaybackMediaSources
    ↓
Probing allowed, non-placeholder source, and STRM or missing primary video/audio stream
    ↓
RefreshMetadata(EnableRemoteContentProbe = true, FullRefresh)
    ↓
Ordinary provider pipeline reads NFO and may invoke other descriptive sources
    ↓
Merge using source state and destination field locks, then save the item
```

The underlying scope error is that **a request to obtain technical information needed for playback enters a full refresh capable of changing descriptive metadata**. Obtaining duration, codecs or resolution should not also authorize importing genres, locations, titles and overviews again.

Code evidence:

- [Upstream playback entry, lines 182–194](https://github.com/jellyfin/jellyfin/blob/08c2ba6e0b6499122822f1c5b18bc426104538b4/Emby.Server.Implementations/Library/MediaSourceManager.cs#L182-L194): STRM or missing primary streams trigger `FullRefresh`; existing primary streams do not bypass the STRM condition.
- [Upstream source merge, lines 908–917](https://github.com/jellyfin/jellyfin/blob/08c2ba6e0b6499122822f1c5b18bc426104538b4/MediaBrowser.Providers/Manager/MetadataService.cs#L908-L917): local source `IsLocked` selects replacement merging, still passing the destination's `LockedFields`. This is not a universal bypass of destination field locks.
- [Production-location merge, lines 1295–1304](https://github.com/jellyfin/jellyfin/blob/08c2ba6e0b6499122822f1c5b18bc426104538b4/MediaBrowser.Providers/Manager/MetadataService.cs#L1295-L1304): a non-replacing merge can also append source locations. The collection fix addresses this separately.

Do not claim every playback overwrites every field. Entry depends on the source/probing conditions; replacement, appending or preservation depends on NFO `lockdata`, destination `LockedFields`, merge mode and providers. Code inspection establishes these paths, but the original item's exact NFO/lock combination must still be recorded rather than assumed.

`ReplaceAllMetadata = false` is not sufficient to isolate descriptive providers. Requiring locks on every API edit is also not a substitute for fixing the playback entry: an unlocked explicit edit should not trigger descriptive reimport merely because playback needs technical information.

## 3. Fix and first-PR boundary

The first PR has one objective: **playback discovery may populate technical data, but may neither reimport descriptions from sources nor overwrite committed descriptions by saving a stale complete item**.

Port these necessary boundaries from the local implementation, adapting them to official code:

1. Pass an explicit technical-only refresh scope from playback; ordinary refresh defaults remain unchanged.
2. Propagate that scope through `BaseItem`, the metadata service and probe providers, bypassing NFO/remote descriptive providers, image work and descriptive maintenance of owned items.
3. FFmpeg may read the media file, but embedded descriptive tags cannot populate titles, genres or other descriptions in this scope, even when those fields are empty.
4. Persist technical fields and related media data without rewriting committed descriptions, locks or version relationships. Playback probing must not advance the timestamp that means descriptive metadata was refreshed.
5. Failed/canceled probing must not first clear valid committed information; synchronize or invalidate caches after persistence.

The technical persistence boundary belongs in the core PR: provider filtering alone is insufficient if a complete-item save can restore stale descriptions. Crash atomicity across all technical tables is a separate enhancement. The core fix does not depend on the new protected-field reset API.

To minimize behavioral changes, the first upstream PR can preserve official decisions about when probing occurs and fix its scope first. Removing unconditional STRM probing, reusing stored streams per source, and revalidation policy can follow independently. The local final implementation already includes these optimizations; that does not make all of them prerequisites of the first PR.

Keep existing clients and editing API compatible. Do not add revision tokens, conditional requests or stale-DTO conflict detection. Do not redefine ordinary user-requested refresh/replace-all behavior. Exclude Moonfin-specific paths, custom fast-import configuration and the translation tool from the official patch.

## 4. Existing evidence and outstanding evidence

| Check | Local evidence | Boundary |
| --- | --- | --- |
| Technical refresh does not invoke NFO/remote descriptive providers | `MetadataServiceRefreshTests.RefreshMetadata_MediaInfoOnly_PreservesEditedMetadata`, including edited/empty fields, first refresh and whole-item locking | Mocked provider tests, not a runtime reproduction on unmodified official code. |
| Embedded tags cannot populate titles, genres or dates | `FFProbeVideoInfoTests.FetchEmbeddedInfo_PlaybackProbe_PreservesEditedAndEmptyFields` | Direct technical-scope field assertions. |
| Repeated reads with primary streams avoid refresh | `MediaSourceManagerTests.GetPlaybackMediaSources_PersistedStreams_DoesNotRefresh`, ordinary paths and STRM | Belongs to the subsequent reuse/probing optimization. |
| Missing primary streams still get probed; source identity is respected | `GetPlaybackMediaSources_MissingVideoStream_ProbesReturnedMediaSource` and `GetPlaybackMediaSources_ReorderedVersions_RefreshesMissingVersionOnly` | Detailed source selection follows separately; the core must preserve relationships. |
| Technical persistence preserves committed descriptions/relationships despite stale input | `ItemPersistenceMediaInfoTests.SaveMediaInfo_StaleDescription_DoesNotOverwriteMetadataOrRelationships` | Independent SQLite test database. |
| Technical scope avoids owned-item maintenance; failed/canceled work restores staged technical state | `MetadataRefreshBoundaryTests` and failure/cancellation cases in `MetadataServiceRefreshTests` | Current local implementation. |
| Real HTTP workflow | The full design records generated-video STRM/fast-symlink import, first/repeated PlaybackInfo after unlocked API edits, cold restart and source changes; the later script covers TV, multipart and version groups | Playback preparation over HTTP, not GUI player end-to-end testing. The latest eight-group script mainly uses protected values and does not replace the earlier unlocked-value case. |

Latest local affected-project validation: **1,934 passed, 13 existing platform skips**. This is a five-project total including other work, not 1,934 newly added core-fix tests. The latest isolated HTTP acceptance script passed eight groups. No real-library editing or refresh was performed.

**Outstanding: run the same minimal reproduction on unmodified official `master` and on an official branch containing only the core patch, demonstrating failure before and success after.** Local passing results must not be described as an already completed official-baseline reproduction or isolated-patch validation.

Before submission:

1. Use an isolated server, a generated short video, STRM and NFO. Disable NFO saving, automatic scans and unrelated providers so the NFO remains unchanged after API edits.
2. Put distinctive old genres and locations in NFO. Use `lockdata=true` with the corresponding destination fields unlocked as a controlled case for the identified replacement branch. Also test `lockdata=false`, distinguishing replacement from append behavior rather than assuming identical results.
3. Edit via the existing full DTO API, reread to confirm committed curated values, and record item/field lock states.
4. Request PlaybackInfo, verify usable primary streams, and reread metadata/logs. The unmodified baseline must demonstrate changed committed values, not merely a refresh log entry.
5. Apply the core patch on that same official base and repeat: technical information works, API-edited descriptions remain, and the descriptive NFO provider is not invoked.
6. Clearly document how an ordinary-file missing-stream fixture is produced. Add protected-empty-value, failure/cancellation and explicit ordinary-refresh checks. Do not claim ordinary official imports naturally omit probing.

## 5. Upstream split and order

| Order | Independent scope | Status / dependency |
| --- | --- | --- |
| **PR 1: core issue** | Separate playback technical discovery from descriptive refresh, including scope propagation, embedded-tag isolation, necessary technical persistence boundaries and regression tests | Highest priority. Create a clean branch from official `master` and establish the before/after evidence. Do not cherry-pick the entire customized local change. |
| PR 2: on-demand probing | Reuse persisted primary streams, change repeated STRM probing, operate per source, verify source changes and explicit revalidation | Implemented locally, not independently ported/validated. Review after PR 1 to separate freshness-policy changes from the core bug. |
| PR 3: missing-data collections | Do not append source locations/studios/tags to populated fields in missing-data mode | `codex/upstream-preserve-metadata-collections`, `7efec6e9d2`; 516 Providers tests passed. Deferred; not a substitute for PR 1. |
| PR 4: NFO file saving | Replace the old NFO only after a temporary replacement is complete | `codex/upstream-nfo-atomic-save`, `d65043f4b0`; 43 XbmcMetadata tests passed. Deferred and independent of the playback entry. |
| Follow-up: refresh coordination/inheritance | Ordinary refresh, owned items, seasons/episodes and merge/split coordination | Split further by independently established official bugs; exclude behaviors unique to the fork. |
| Separate designs: advanced consistency | Cross-technical-table crash transactions; explicit protected-field reset API | Distinct review scopes requiring design discussion. Neither needs to land before the playback bug fix. |

Within PR 1, use three coherent commits for review: (1) focused regression fixtures; (2) refresh scope/provider restrictions; (3) persistence boundary plus failure/compatibility tests. The complete PR must pass; an entry flag without embedded-tag and persistence protection is insufficient.

All PRs target official `jellyfin/jellyfin:master`. Reprioritizing does not delete the two prepared branches, and they should not be published first. Official contribution policy still requires submitter review and the submitter's own explanation; this is engineering reference material, not an AI-generated PR body to post verbatim.
