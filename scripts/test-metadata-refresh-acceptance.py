#!/usr/bin/env python3
"""Opt-in HTTP acceptance tests. Always creates its own server/data/media on loopback.

Requires Python 3, dotnet, a built Jellyfin server DLL and ffmpeg. No existing server
URL or credentials are accepted. Retains the temporary fixture and a redacted report
for diagnosis. Never points a library at files outside that fixture.
"""
import argparse
import concurrent.futures
import json
import os
from pathlib import Path
import secrets
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server-dll", type=Path, required=True)
    parser.add_argument("--ffmpeg", type=Path, required=True)
    args = parser.parse_args()
    if os.name != "posix" or os.geteuid() == 0:
        parser.error("Write-denial acceptance requires a non-root POSIX host")
    root = Path(tempfile.mkdtemp(prefix="jellyfin-refresh-acceptance-"))
    os.chmod(root, 0o700)
    for name in ("data", "config", "cache", "log", "source", "movies", "tv", "saved"):
        (root / name).mkdir()
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        port = sock.getsockname()[1]
    (root / "config/network.xml").write_text(
        f'<NetworkConfiguration><InternalHttpPort>{port}</InternalHttpPort>'
        '<LocalNetworkAddresses><string>127.0.0.1</string></LocalNetworkAddresses>'
        '<EnableRemoteAccess>false</EnableRemoteAccess></NetworkConfiguration>')
    source = root / "source/source.mkv"
    subprocess.run([str(args.ffmpeg.resolve()), "-y", "-hide_banner", "-loglevel", "error",
                    "-f", "lavfi", "-i", "color=c=black:s=160x90:r=10", "-t", "1",
                    "-c:v", "libx264", "-metadata", "title=Embedded original",
                    "-metadata", "genre=Embedded original", str(source)], check=True)

    def nfo(path, tag, title, extra=""):
        path.write_text(f'<{tag}><title>{title}</title><genre>Original</genre>'
                        f'<country>Original</country><plot>Original overview</plot>{extra}</{tag}>')

    for name in ("A", "B", "C", "D", "Stack", "Auto A", "Auto C"):
        folder = root / "movies" / name
        folder.mkdir()
        suffixes = (" - cd1", " - cd2") if name == "Stack" else (" - 1080p", " - 2160p") if name.startswith("Auto ") else ("",)
        for suffix in suffixes:
            (folder / (name + suffix + ".mkv")).symlink_to(source)
        nfo(folder / "movie.nfo", "movie", name)
    series_dir = root / "tv/Fixture Series"
    season_dir = series_dir / "Season 01"
    season_dir.mkdir(parents=True)
    nfo(series_dir / "tvshow.nfo", "tvshow", "Fixture Series")
    nfo(season_dir / "season.nfo", "season", "Season One", "<seasonnumber>1</seasonnumber>")
    for number in (1, 2):
        name = f"Fixture Series - S01E{number:02}"
        (season_dir / (name + ".mkv")).symlink_to(source)
        nfo(season_dir / (name + ".nfo"), "episodedetails", f"Episode {number}",
            f"<season>1</season><episode>{number}</episode>")
    saved = root / "saved/Save Failure"
    saved.mkdir()
    (saved / "Save Failure.mkv").symlink_to(source)
    save_path = saved / "movie.nfo"
    nfo(save_path, "movie", "Save Failure")
    console = (root / "console.log").open("w")
    process = subprocess.Popen([
        "dotnet", str(args.server_dll.resolve()), "--datadir", str(root / "data"),
        "--configdir", str(root / "config"), "--cachedir", str(root / "cache"),
        "--logdir", str(root / "log"), "--nowebclient", "--nonetchange",
        "--ffmpeg", str(args.ffmpeg.resolve())], stdout=console, stderr=subprocess.STDOUT)
    base = f"http://127.0.0.1:{port}"
    token = None
    checks = []
    completed = False

    def api(path, method="GET", body=None):
        auth = 'MediaBrowser Client="Acceptance",Device="Test",DeviceId="isolated",Version="1"'
        if token:
            auth += f',Token="{token}"'
        request = urllib.request.Request(
            base + path, method=method,
            data=None if body is None else json.dumps(body).encode(),
            headers={"Content-Type": "application/json", "Authorization": auth})
        with urllib.request.urlopen(request, timeout=60) as response:
            data = response.read()
            return json.loads(data) if data else None

    def wait(condition, label):
        deadline = time.monotonic() + 90
        while time.monotonic() < deadline:
            if process.poll() is not None:
                raise AssertionError("Isolated server exited: " + str(root / "console.log"))
            result = condition()
            if result:
                return result
            time.sleep(.25)
        raise AssertionError("Timeout: " + label)

    def passed(label):
        checks.append(label)
        print("PASS " + label, flush=True)

    try:
        def ready():
            try:
                return isinstance(api("/Startup/User"), dict)
            except (urllib.error.URLError, TimeoutError, json.JSONDecodeError):
                return False
        wait(ready, "startup")
        api("/Startup/User")
        password = secrets.token_urlsafe(24)
        api("/Startup/User", "POST", {"Name": "acceptance", "Password": password})
        api("/Startup/Configuration", "POST", {"ServerName": "Isolated acceptance", "UICulture": "en-US"})
        api("/Startup/Complete", "POST")
        login = api("/Users/AuthenticateByName", "POST", {"Username": "acceptance", "Pw": password})
        token, uid = login["AccessToken"], login["User"]["Id"]
        assert api("/System/Info/Public")["ServerName"] == "Isolated acceptance"
        for name, kind in (("movies", "movies"), ("tv", "tvshows"), ("saved", "movies")):
            query = urllib.parse.urlencode({"name": name, "collectionType": kind,
                                          "paths": str(root / name), "refreshLibrary": "false"})
            api("/Library/VirtualFolders?" + query, "POST", {"LibraryOptions": {
                "EnableInternetProviders": False, "EnableRealtimeMonitor": False,
                "LocalMetadataOnlyImport": True, "MetadataSavers": [],
                "AutomaticRefreshIntervalDays": 0}})
        # Startup completion can still be running its initial empty-library scan.
        wait(lambda: not any(t["State"] in ("Running", "Cancelling") for t in api("/ScheduledTasks")
                             if t["Key"] == "RefreshLibrary"), "initial scan completion")
        api("/Library/Refresh", "POST")
        def listing():
            return api("/Items?recursive=true&includeItemTypes=Movie,Series,Season,Episode&userId=" + uid)["Items"]
        wait(lambda: len(listing()) >= 12, "TV and movie import")
        items = listing()
        ids = {item["Name"]: item["Id"] for item in items}
        def get(item_id):
            return api(f"/Users/{uid}/Items/{item_id}")
        def edit(item_id, **values):
            dto = get(item_id)
            dto.update(values)
            api("/Items/" + item_id, "POST", dto)
        def protect(item_id):
            edit(item_id, Genres=["类型-" + item_id[:6]], ProductionLocations=[],
                 LockedFields=["Genres", "ProductionLocations"], Overview="Refresh marker")
        def verify(item_id):
            dto = get(item_id)
            assert dto.get("Genres") == ["类型-" + item_id[:6]], (dto["Name"], dto.get("Genres"))
            assert dto.get("ProductionLocations", []) == [], dto["Name"]
            assert {"Genres", "ProductionLocations"} <= set(dto["LockedFields"])
            return dto
        def refresh(item_id, recursive=False):
            api(f"/Items/{item_id}/Refresh?metadataRefreshMode=FullRefresh&replaceAllMetadata=true&recursive={str(recursive).lower()}", "POST")
        def playback(item_id):
            data = api(f"/Items/{item_id}/PlaybackInfo?UserId={uid}", "POST", {})
            assert any(s["Type"] == "Video" and s.get("Width") == 160
                       for s in data["MediaSources"][0]["MediaStreams"])
        tv_ids = [i["Id"] for i in items if i["Type"] in ("Series", "Season", "Episode")]
        assert len(tv_ids) == 4, tv_ids
        for item_id in tv_ids:
            protect(item_id)
        for item in items:
            if item["Type"] == "Episode":
                playback(item["Id"])
                verify(item["Id"])
        series_id = next(i["Id"] for i in items if i["Type"] == "Series")
        refresh(series_id, recursive=True)
        wait(lambda: all(get(i).get("Overview") == "Original overview" for i in tv_ids), "recursive series refresh")
        for item_id in tv_ids:
            verify(item_id)
        passed("Series/season/episode import, first playback and recursive replace-all preserve independent locks and empty locations")
        before = {i: get(i) for i in tv_ids}
        api(f"/Items/{series_id}/Refresh?metadataRefreshMode=None&imageRefreshMode=FullRefresh&recursive=true", "POST")
        # Subsequent full refresh of the series is an ordered queue completion barrier.
        edit(series_id, Overview="Refresh marker")
        refresh(series_id)
        wait(lambda: get(series_id).get("Overview") == "Original overview", "image queue barrier")
        for item_id in tv_ids:
            after = verify(item_id)
            assert after.get("IndexNumber") == before[item_id].get("IndexNumber")
        passed("Image-only series refresh leaves protected fields and episode/season numbers unchanged")
        stack = ids["Stack"]
        parts = api(f"/Videos/{stack}/AdditionalParts?userId={uid}")["Items"]
        assert len(parts) == 1, parts
        part = parts[0]["Id"]
        for item_id in (stack, part):
            protect(item_id)
            playback(item_id)
            verify(item_id)
        refresh(stack)
        wait(lambda: get(stack).get("Overview") == "Original overview", "multipart refresh")
        for item_id in (stack, part):
            verify(item_id)
        assert [p["Id"] for p in api(f"/Videos/{stack}/AdditionalParts?userId={uid}")["Items"]] == [part]
        passed("Multipart playback and owner refresh retain part identity and independent protected values")
        videos = [ids[n] for n in ("A", "B", "C", "D")]
        for item_id in videos:
            protect(item_id)
        def merge(members):
            api("/Videos/MergeVersions?ids=" + ",".join(members), "POST")
        merge(videos[:2])
        merge(videos[2:])
        merge([videos[1], videos[3]])
        assert all({s["Id"] for s in get(i)["MediaSources"]} == set(videos) for i in videos)
        for _ in range(3):
            with concurrent.futures.ThreadPoolExecutor(max_workers=5) as pool:
                futures = [pool.submit(refresh, i) for i in videos]
                futures.append(pool.submit(api, f"/Videos/{videos[1]}/AlternateSources", "DELETE"))
                for future in futures:
                    future.result()
            for item_id in videos:
                verify(item_id)
                assert len(get(item_id)["MediaSources"]) == 1
            merge(videos)
        wait(lambda: all(get(i).get("Overview") == "Original overview" for i in videos), "concurrent refresh completion")
        for item_id in videos:
            verify(item_id)
            assert {s["Id"] for s in get(item_id)["MediaSources"]} == set(videos)
        passed("Existing groups merge through secondary IDs; repeated refresh/split/remerge preserves all four members")
        auto_roots = [ids["Auto A"], ids["Auto C"]]
        auto_members = [source["Id"] for i in auto_roots for source in get(i)["MediaSources"]]
        assert len(set(auto_members)) == 4
        local_members = [i for i in auto_members if i not in auto_roots]
        for item_id in auto_members:
            protect(item_id)
        merge(local_members)
        for item_id in auto_members:
            dto = verify(item_id)
            assert dto["MediaSourceCount"] == 4
            assert {s["Id"] for s in dto["MediaSources"]} == set(auto_members)
        with concurrent.futures.ThreadPoolExecutor(max_workers=5) as pool:
            futures = [pool.submit(refresh, i) for i in auto_roots]
            futures.append(pool.submit(api, f"/Videos/{local_members[0]}/AlternateSources", "DELETE"))
            for future in futures:
                future.result()
        wait(lambda: all(get(i).get("Overview") == "Original overview" for i in auto_roots), "automatic group owner refresh")
        for item_id in auto_members:
            dto = verify(item_id)
            assert dto["MediaSourceCount"] == 2
            assert len(dto["MediaSources"]) == 2
        passed("Automatic file groups merge through local secondary IDs without duplicates and split back into two protected groups during refresh")
        # Enable NFO saving only for this generated library after its initial import.
        folder = next(f for f in api("/Library/VirtualFolders") if f["Name"] == "saved")
        options = folder["LibraryOptions"]
        options.update(LocalMetadataOnlyImport=False, MetadataSavers=["Nfo"])
        api("/Library/VirtualFolders/LibraryOptions", "POST", {"Id": folder["ItemId"], "LibraryOptions": options})
        original = save_path.read_bytes()
        # Directory permissions block both temporary-file and direct-file writes; never chmod real media.
        saved.chmod(0o500)
        try:
            protect(ids["Save Failure"])
            assert save_path.read_bytes() == original
            verify(ids["Save Failure"])
            refresh(ids["Save Failure"])
            # Ordinary full refresh may also replace the unlocked overview from embedded tags.
            wait(lambda: get(ids["Save Failure"]).get("Overview") != "Refresh marker", "failed NFO saver refresh")
            assert save_path.read_bytes() == original
            verify(ids["Save Failure"])
            wait(lambda: "Error in metadata saver" in (root / "console.log").read_text(), "NFO failure diagnostic")
        finally:
            saved.chmod(0o700)
        edit(ids["Save Failure"], Overview="Writable again")
        written = save_path.read_text(encoding="utf-8-sig")
        assert "类型-" in written and "ProductionLocations" in written and "Writable again" in written
        passed("NFO write denial is logged; committed values survive stale NFO refresh; later successful save contains values and locks")
        # Explicit resets are synchronous and preserve locks, other fields and version links.
        reset_id = ids["A"]
        before = get(reset_id)
        assert "MetadataRevision" not in before
        request = {"ProviderName": "Nfo", "Fields": ["Genres", "ProductionLocations"]}

        def expect_reset_error(code, payload):
            try:
                api(f"/Items/{reset_id}/Metadata/Reset", "POST", payload)
            except urllib.error.HTTPError as error:
                assert error.code == code, (error.code, error.read().decode())
            else:
                raise AssertionError("Expected reset error " + str(code))

        expect_reset_error(400, dict(request, ProviderName="Missing provider"))
        api(f"/Items/{reset_id}/Metadata/Reset", "POST", request)
        after = get(reset_id)
        assert after["Genres"] == ["Original"] and after["ProductionLocations"] == ["Original"]
        for field in ("LockedFields", "LockData", "Name", "Overview", "MediaSourceCount"):
            assert after.get(field) == before.get(field), field
        reset_nfo = root / "movies/A/movie.nfo"
        original_reset_nfo = reset_nfo.read_bytes()
        try:
            reset_nfo.write_text('<movie><title>A</title><genre>Replacement</genre></movie>')
            expect_reset_error(422, request)
            assert get(reset_id)["Genres"] == ["Original"]
            edit(reset_id, LockData=True)
            api(f"/Items/{reset_id}/Metadata/Reset", "POST", dict(request, AllowEmptyValues=True))
            after = get(reset_id)
            assert after["Genres"] == ["Replacement"] and not after.get("ProductionLocations")
            assert after["LockData"] and after["LockedFields"] == before["LockedFields"]
            reset_nfo.write_text('<movie><invalid')
            expect_reset_error(422, request)
            assert get(reset_id)["Genres"] == ["Replacement"]
        finally:
            reset_nfo.write_bytes(original_reset_nfo)
        with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
            futures = [pool.submit(api, f"/Items/{reset_id}/Metadata/Reset", "POST", request),
                       pool.submit(api, f"/Items/{reset_id}/Refresh?metadataRefreshMode=FullRefresh&replaceAllMetadata=true", "POST")]
            for future in futures:
                future.result()
        assert get(reset_id)["Genres"] == ["Original"]
        assert get(reset_id)["LockData"]
        passed("Atomic source reset preserves locks and other fields, rejects empty/failed sources, supports explicit clearing and coexists with refresh; legacy edits need no version")
        # A reset commits the database before attempting the NFO mirror.
        nfo(save_path, "movie", "Save Failure", '<genre>Reset source</genre>')
        saved.chmod(0o500)
        try:
            api(f"/Items/{ids['Save Failure']}/Metadata/Reset", "POST", {"ProviderName": "Nfo", "Fields": ["Genres"]})
            value = get(ids["Save Failure"])
            assert "Reset source" in value["Genres"] and "Genres" in value["LockedFields"]
        finally:
            saved.chmod(0o700)
        passed("NFO save failure after explicit reset does not roll back the committed protected database values")
        completed = True
    finally:
        saved.chmod(0o700)
        if process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=30)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()
        console.close()
        (root / "report.json").write_text(json.dumps({"completed": completed, "checks": checks}, ensure_ascii=False, indent=2))
        print("Fixture and redacted report: " + str(root), flush=True)


if __name__ == "__main__":
    main()
