"""Run disposable retail and Steam discovery fixtures in an offline Wine container."""
from contextlib import contextmanager
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
from tempfile import TemporaryDirectory


OUTPUT = Path('/out')
LICENSED = Path('/data')
RETAIL = r'Software\Activision\Call of Duty 2'
LEGACY32 = 'HKLM\\' + RETAIL
TEST_KEYS = (b'11111111111111114D09', b'000000000000000086D3')
ENV = dict(os.environ, LC_ALL='C.UTF-8', WINEARCH='win64', WINEDEBUG='-all', WINEDLLOVERRIDES='mscoree,mshtml=',
           COD2_CRASH_NOPOPUP='1', COD2X_TEST_SERVICE_URL='http://127.0.0.1:9',
           COD2X_TEST_UPDATER_ADDRESS='127.0.0.1:9',
           COD2X_TEST_UPDATER_URL='http://127.0.0.1:9/api/updater/check')


def win(path):
    return 'Z:' + str(path).replace('/', '\\')


def digest(path, cache):
    hashes = {}
    for item in sorted(path.rglob('*')):
        if item.is_file():
            target = item.resolve()
            if target not in cache:
                with item.open('rb') as file:
                    cache[target] = hashlib.file_digest(file, 'sha256').hexdigest()
            hashes[str(item.relative_to(path))] = cache[target]
    return hashes


def synthetic_install(path):
    main = path / 'main'
    main.mkdir(parents=True)
    for archive in (LICENSED / 'main').glob('*.iwd'):
        (main / archive.name).symlink_to(archive)
    profile = main / 'players/legacy'
    profile.mkdir(parents=True)
    (profile.parent / 'active.txt').write_text('legacy\n')
    (profile / 'config_mp.cfg').write_text('echo P5_LEGACY_PROFILE_WAS_READ\n')
    (main / 'server.cfg').write_text('echo P5_LEGACY_CONFIG_WAS_READ\n')
    return path


def command(env, *args, cwd=None, check=True):
    return subprocess.run(args, env=env, cwd=cwd, check=check, timeout=90,
                          stdout=subprocess.PIPE, stderr=subprocess.STDOUT)


@contextmanager
def wine_context(run, user=None):
    home = run / 'unix-home'
    home.mkdir()
    with TemporaryDirectory(prefix='prefix-', dir=run) as prefix:
        env = dict(ENV, WINEPREFIX=prefix, HOME=str(home), CFFIXED_USER_HOME=str(home))
        if user is not None:
            env['USER'] = user
        try:
            subprocess.run(['wineserver', '-p'], env=env, stdout=subprocess.DEVNULL,
                           stderr=subprocess.DEVNULL, timeout=15, check=True)
            with (run / 'wineboot.log').open('wb') as log:
                subprocess.run(['wineboot', '-i'], env=env, stdout=log, stderr=subprocess.STDOUT,
                               timeout=120, check=True)
            yield env, Path(prefix)
        finally:
            command(env, 'wineserver', '-k', check=False)
            command(env, 'wineserver', '-w')


def registry(env, key, value, text, view=None):
    args = ['wine', 'reg', 'add', key, '/v', value, '/t', 'REG_SZ', '/d', text, '/f']
    if view:
        args.append(f'/reg:{view}')
    command(env, *args)


def steam_setup(env, run):
    steam = run / 'Steam Primary'
    library = run / 'Steam Library Żółć 東京'
    game = synthetic_install(library / 'steamapps/common/Fixture CoD2')
    (steam / 'steamapps').mkdir(parents=True)
    (steam / 'steamapps/libraryfolders.vdf').write_text(
        f'"libraryfolders" {{ "0" {{ "path" {json.dumps(win(steam), ensure_ascii=False)} }}'
        f' "1" {{ "path" {json.dumps(win(library), ensure_ascii=False)} "apps" {{ "2630" "1" }} }} }}\n')
    (library / 'steamapps/appmanifest_2630.acf').write_text(
        '"AppState" { "appid" "2630" "installdir" "Fixture CoD2" }\n')
    registry(env, r'HKCU\Software\Valve\Steam', 'SteamPath', win(steam))
    return game


def app_data(prefix):
    users = prefix / 'drive_c/users'
    return next(user / 'AppData/Local' for user in users.iterdir() if user.name != 'Public')

def assert_result(run, result, source, selected):
    log = result.stdout.decode('utf-8', 'replace')
    (run / 'engine.log').write_bytes(result.stdout)
    if any(key in result.stdout for key in TEST_KEYS):
        raise ValueError('synthetic key appeared in engine output')
    if source is None:
        if result.returncode != 1 or 'main/iw_00.iwd through main/iw_15.iwd' not in log:
            raise ValueError(f'expected missing data error, got {result.returncode}. See {run}/engine.log')
        return
    if result.returncode or f'Game data ({source}): ' not in log or 'User data: ' not in log:
        raise ValueError(f'expected {source}, got process status {result.returncode}. See {run}/engine.log')
    if win(selected).casefold() not in log.casefold():
        raise ValueError(f'expected data at {selected}. See {run}/engine.log')
    if 'P5_LEGACY_CONFIG_WAS_READ' in log or 'P5_LEGACY_PROFILE_WAS_READ' in log:
        raise ValueError('legacy config was read without import')


def check(case, edition, licensed_cache):
    run = OUTPUT / f'{edition}-{case}'
    run.mkdir()
    executable = Path(f'/matrix/ded64-{edition}/cod2_win32_ded.exe')
    candidates = [LICENSED]
    source, selected, args = None, None, []
    user_name = 'Zoë 東京' if case == 'unicode-long' else None
    with wine_context(run, user_name) as (env, prefix):
        app = app_data(prefix)
        game_home = app / 'CoD2 Unbound'
        cwd = run / 'unrelated-cwd'
        cwd.mkdir()
        if case in ('retail32', 'retail64', 'both', 'invalid-explicit'):
            retail = synthetic_install(run / 'Retail Install')
            candidates.append(retail)
            view = 64 if case == 'retail64' else 32
            registry(env, LEGACY32, 'InstallPath', win(retail), view)
            selected, source = retail, f'retail registry ({view}-bit)'
        if case in ('steam', 'both'):
            selected, source = steam_setup(env, run), 'Steam'
            candidates.append(selected)
        if case == 'exec-folder':
            selected, source = synthetic_install(run / 'Portable'), 'executable folder'
            candidates.append(selected)
            executable = Path(shutil.copyfile(executable, selected / executable.name))
        if case in ('read-only', 'unicode-long', 'key', 'unsafe-home'):
            selected, source = LICENSED, 'command line'
            if case == 'unicode-long':
                path = run / 'Żółć + 東京' / Path(*['licensed-' + 'y' * 65] * 4)
                selected = synthetic_install(path)
                candidates.append(selected)
                game_home = game_home.joinpath(*['profile-' + 'x' * 65] * 4)
                windows_home = 'C:/' + game_home.relative_to(prefix / 'drive_c').as_posix()
                if user_name not in app.parts or len(win(selected)) <= 260 or len(windows_home) <= 260:
                    raise ValueError('fixture requires a Unicode user name and paths over MAX_PATH')
                args += ['+set', 'fs_homepath', windows_home]
            args += ['+set', 'fs_basepath', win(selected)]
        if case == 'invalid-explicit':
            args += ['+set', 'fs_basepath', win(run / 'missing')]
            source = None
        if case == 'unsafe-home':
            args += ['+set', 'fs_homepath', win(LICENSED)]
        if case == 'key':
            result = command(env, 'wine', str(OUTPUT / 'key-probe.exe'), check=False)
            (run / 'key-probe.log').write_bytes(result.stdout)
            if result.returncode:
                raise ValueError(f'key probe exited {result.returncode}. See {run}/key-probe.log')
        virtual = app / 'VirtualStore/Program Files (x86)/Activision/Call of Duty 2/main/players/legacy'
        virtual.mkdir(parents=True)
        (virtual / 'config_mp.cfg').write_text('echo P5_LEGACY_PROFILE_WAS_READ\n')
        candidates.append(app / 'VirtualStore')
        cache = dict(licensed_cache)
        before = {str(path): digest(path, cache) for path in candidates}
        result = command(env, 'wine', str(executable), *args, '+set', 'developer', '0',
                         '+set', 'logfile', '2', '+exec', 'server.cfg', '+writeconfig', 'p5check.cfg',
                         '+quit', cwd=cwd, check=False)
        if case == 'unsafe-home':
            (run / 'engine.log').write_bytes(result.stdout)
            if result.returncode != 1 or b'under LocalAppData' not in result.stdout:
                raise ValueError('writable override outside LocalAppData was accepted')
        else:
            assert_result(run, result, source, selected)
        if case == 'key':
            checked = command(env, 'wine', str(OUTPUT / 'key-probe.exe'), 'verify', check=False)
            (run / 'key-after-engine.log').write_bytes(checked.stdout)
            if checked.returncode:
                raise ValueError(f'key preservation probe exited {checked.returncode}')
        cache = dict(licensed_cache)
        after = {str(path): digest(path, cache) for path in candidates}
        for name, hashes in (('before', before), ('after', after)):
            (run / f'{name}.json').write_text(json.dumps(hashes, ensure_ascii=False, indent=2) + '\n')
        if before != after or any(cwd.iterdir()):
            raise ValueError('licensed data, VirtualStore or startup directory changed')
        writes = sorted(str(path.relative_to(game_home)) for path in game_home.rglob('*') if path.is_file())
        (run / 'writes.json').write_text(json.dumps(writes, indent=2) + '\n')
        if source and case != 'unsafe-home':
            if not any(path.endswith('/p5check.cfg') for path in writes) or any('legacy' in path for path in writes):
                raise ValueError(f'expected generated config and no legacy profile, got {writes}')
        elif writes:
            raise ValueError(f'failed launch wrote user data: {writes}')
        print(f'PASS {edition}/{case} ({source or "refused"}, writes={writes}, hashes unchanged)', flush=True)


def main():
    subprocess.run(['x86_64-w64-mingw32-gcc', '-DCOD2_WINDOWS_NATIVE=1', '-I/src/src', '-I/src/src/headers',
                    '/src/tools/windows/path-test-key.c', '/src/src/win32/win_key.c', '-ladvapi32',
                    '-o', str(OUTPUT / 'key-probe.exe')], check=True, timeout=60)
    # /data is mounted read-only. Hash it at both ends of the run and reuse
    # its fingerprints for the fixture links in each case.
    licensed_cache = {}
    original_hashes = digest(LICENSED, licensed_cache)
    failures = []
    for edition in ('original', 'cod2x'):
        for case in ('retail32', 'retail64', 'steam', 'both', 'neither', 'exec-folder',
                     'invalid-explicit', 'read-only', 'key', 'unicode-long', 'unsafe-home'):
            try:
                check(case, edition, licensed_cache)
            except (OSError, RuntimeError, ValueError, subprocess.SubprocessError) as error:
                print(f'FAIL {edition}/{case}: {error}', file=sys.stderr, flush=True)
                failures.append((edition, case))
    if original_hashes != digest(LICENSED, {}):
        print('FAIL: the read-only licensed file manifest changed', file=sys.stderr)
        return True
    return bool(failures)


if __name__ == '__main__':
    sys.exit(main())
