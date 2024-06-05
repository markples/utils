import argparse
import distutils
import itertools
import json
import os
import pathlib
import re
import subprocess
import sys

import asp_envs

gcbuild_path = pathlib.Path(__file__).parent.resolve()

class ChDir(object):
    def __init__(self, path):
        self.path = path

    def __enter__(self):
        self.restore = os.getcwd()
        os.chdir(self.path)

    def __exit__(self, type, value, traceback):
        os.chdir(self.restore)

    
class EnvVars(object):
    def __init__(self, **kvargs):
        self.vars = kvargs

    def _update_os_env(self, k, v):
        if v is None:
            print(f"[ENV]: clearing {k}")
            del os.environ[k]
        else:
            print(f"[ENV]: setting {k} to {v}")
            os.environ[k] = v

    def __enter__(self):
        self.restore = dict(os.environ)
        for k, v in self.vars.items():
            self._update_os_env(k, v)

    def __exit__(self, type, value, traceback):
        for k in self.vars.keys():
            v = self.restore.get(k)
            self._update_os_env(k, v)

def get_next_file_name(path, file):
    filename = pathlib.Path(file).name
    next_file_base = pathlib.Path(path) / filename
    for i in itertools.count(start=1):
        attempt = next_file_base.parent / (next_file_base.name + i)
        if not attempt.exists():
            return attempt

def parse():
    parser = argparse.ArgumentParser(
        prog='gcbuild',
        description='Tool for building and preserving clr gc',
    )

    parser.add_argument('-a', '--all', action='store_true') # build clr+libs instead of clr.native
    parser.add_argument('-c', '--configuration', default='release', choices=['debug', 'checked', 'release'])
    parser.add_argument('-b', '--build', action='store_true')
    parser.add_argument('--build-only', action='store_true')
    parser.add_argument('--allow-local-changes', action='store_true')
    parser.add_argument('-t', '--build-tests', action='store_true')

    parser.add_argument('-r', '--run', action='append', choices=['rf', 'micro', 'asp', 'gcperfsim', 'gcperfsim-file', 'gcperfsim-cmd'])
    parser.add_argument('--iterations', default=4, type=int, help='number of iterations, currently -r asp only')

    aspnet_yaml = gcbuild_path.joinpath('ASPNetBenchmarks.yaml.template')
    aspnet_benchmarks_csv = gcbuild_path.joinpath('aspnet.all.csv')

    parser.add_argument('--asp-template', default=str(aspnet_yaml))
    parser.add_argument('--asp-benchmarks', default=str(aspnet_benchmarks_csv))
    parser.add_argument('--asp-include', action='append', help='benchmarks to include (regex)')
    parser.add_argument('--asp-exclude', action='append', help='benchmarks to exclude (regex)')

    parser.add_argument('--gcperfsim-dir', default=str(gcbuild_path.joinpath('gcperfsim')))
    parser.add_argument('--gcperfsim-file', default='GCPerfSim_NW_F.yaml')

    parser.add_argument('--trace-type', default='gc', choices=['gc', 'verbose', 'cpu', 'threadtime', 'none'])
    parser.add_argument('--testmix-time', default="00:01:00")

    parser.add_argument('runtime_root')
    parser.add_argument('save_root')
    parser.add_argument('save_family')

    parser.add_argument('arg_save_name_0', metavar='save_name', help='save_name[+suffix]')
    parser.add_argument('--save-name', dest='arg_save_name', action='append', help='additional save_name(s)')

    args = parser.parse_args()

    if not args.arg_save_name:
        args.arg_save_name = []
    args.arg_save_name.insert(0, args.arg_save_name_0)
    args.save_names = [save_name.split('+', maxsplit=1) for save_name in args.arg_save_name]
    args.save_names = [save_name if len(save_name) > 1 else [save_name[0], ""] for save_name in args.save_names]

    return args

def validate(args):
    if not os.path.isdir(args.runtime_root):
        raise Exception(f"{args.runtime_root} does not exist")

    args.save_family_loc = os.path.join(args.save_root, args.save_family)
    args.save_loc = os.path.join(args.save_family_loc, args.save_names[0][0])
    args.output_suffix_use = "-" + args.save_names[0][1] if args.save_names[0][1] else ""
    args.build_and_copy = args.build and not args.build_only
    args.allow_local_changes = args.allow_local_changes or args.build_only

    if args.build_and_copy:
        if not args.build_only and os.path.exists(args.save_loc):
            save_contents = os.listdir(args.save_loc)
            if save_contents and (save_contents != ['gc']):
                raise Exception(f"{args.save_loc} already exists with {os.listdir(args.save_loc)}")

        with ChDir(args.runtime_root):
            git_status = subprocess.run(f"git status --porcelain", capture_output=True, check=True)

            # hacky place for this
            args.has_local_changes = True if git_status.stdout else False
            if args.has_local_changes and not args.allow_local_changes:
                subprocess.run(f"git status", check=True)
                raise Exception(f"repo has uncommitted changes")

            git_rev = subprocess.run(f"git rev-parse HEAD", capture_output=True, text=True, check=True)
            args.commit = git_rev.stdout.strip()
            
    if args.run and not os.path.exists(args.save_loc) and not args.build_and_copy:
        raise Exception(f"{args.save_loc} does not exist")

def setup_vals(args):
    args.gc_dir = os.path.join(args.save_loc, "gc")
    args.artifacts_root = f'{args.runtime_root}\\artifacts\\tests\\coreclr\\windows.x64.{args.configuration}'
    args.core_root = f'{args.artifacts_root}\\Tests\\Core_Root'

def setup_dirs(args):
    os.makedirs(args.save_root, exist_ok=True)
    os.makedirs(args.save_family_loc, exist_ok=True)
    os.makedirs(args.save_loc, exist_ok=True)
    os.makedirs(args.gc_dir, exist_ok=True)

# (binary name, disasm it?)
binaries = [('clrgc.dll', True), ('clrgcexp.dll', True), ('coreclr.dll', False)]

def build(args):
    os.chdir(args.runtime_root)
    target = 'clr+libs' if args.all else 'clr.native'
    subprocess.run(f'build.cmd -c {args.configuration} -lc release {target}', check=True)
    subprocess.run(f'src\\tests\\build.cmd generatelayoutonly x64 {args.configuration} /p:LibrariesConfiguration=Release', check=True)
    if args.build_and_copy:
        subprocess.run(f'git tag gcbuild-{args.save_family}-{args.save_names[0][0]}')

def build_tests(args):
    os.chdir(f'{args.runtime_root}\\src\\tests')
    subprocess.run(f'build.cmd {args.configuration} test GC\\Stress\\Framework\\ReliabilityFramework.csproj', check=True)

def copy(args):
    os.chdir(args.runtime_root)
    print()
    print(f'Tagging')
    data = {
        'commit': args.commit,
        'local-changes': args.has_local_changes,
        'configuration': args.configuration,
        'local-enlistment': args.runtime_root,
    }

    with open(f'{args.save_loc}\\data.json', 'w') as f:
        json.dump(data, f, indent=4)
    for k, v in data.items():
        if k != 'local-enlistment':
            with open(f'{args.save_loc}\\{k}-{v}', 'w'):
                pass

    print(f'Copying "src\\coreclr\\gc" to "{args.gc_dir}"')
    distutils.dir_util.copy_tree(f'src\\coreclr\\gc', args.gc_dir)

    print(f'Copying binaries from "{args.core_root}" to "{args.save_loc}"')
    for binary, _ in binaries:
        root, _ = os.path.splitext(binary)
        distutils.file_util.copy_file(f'{args.core_root}\\{binary}', args.save_loc)
        distutils.file_util.copy_file(f'{args.core_root}\\PDB\\{root}.pdb', args.save_loc)
    print(f'Disassembling binaries')

    for binary in (b for b, disasm in binaries if disasm):
        root, _ = os.path.splitext(binary)
        with open(f'{args.save_loc}\\{root}.asm', 'w') as f:
            subprocess.run(f'dumpbin /disasm {args.save_loc}\\{binary}', stdout=f, check=True)

def run(args):
    if args.run:
        setup = setup_run(args)
        if 'rf' in args.run:
            with setup:
                run_rf(args)
        if 'micro' in args.run:
            run_micro(args)
        if 'asp' in args.run:
            run_asp(args)
        if 'gcperfsim' in args.run:
            run_gcperfsim(args)
        if 'gcperfsim-file' in args.run:
            with setup:
                run_gcperfsim_file(args)
        if 'gcperfsim-cmd' in args.run:
            with setup:
                run_gcperfsim_cmd(args)

def setup_run(args):
    src = f'{args.save_loc}\\clrgcexp.dll'
    gc_name = f'clrgcexp_{args.save_names[0][0]}.dll'
    dst = f'{args.core_root}\\{gc_name}'
    print(f'Copying GC from {src} to {dst}')
    distutils.file_util.copy_file(src, dst)
    return EnvVars(complus_gcname = gc_name, CORE_ROOT = args.core_root)

def specialize(args, file, replacements=None):
    ending = '.template'
    if file.endswith(ending):
        new_file = file[:-len(ending)]
    else:
        new_file = file + '.specific'

    with open(file, 'r') as r, open(new_file, 'w') as w:
        for line in r:
            line = (
                line
                .replace('{testmix_time}', args.testmix_time)
                .replace('{save_root}', args.save_root)
                .replace('{save_family}', args.save_family)
                .replace('{save_name}', args.save_names[0][0])
                .replace('{core_root}', args.core_root)
                .replace('{trace_type}', args.trace_type)
                .replace('{output_suffix}', args.output_suffix_use)
            )
            if replacements:
                # Multi-line?
                line = line.format(**replacements)

            w.write(line)
    return new_file

def run_rf(args):
    os.chdir(f'{args.artifacts_root}\\GC\Stress\Framework\ReliabilityFramework')
    template = 'C:\\r\\utils\\gcbuild\\testmix_gc_ci.config.template'
    specific = specialize(args, template)
    subprocess.run(f'ReliabilityFramework.cmd -coreroot {args.core_root} {specific}')

def run_micro(args):
    template = 'C:\\r\\utils\\gcbuild\\Microbenchmarks.yaml.template'
    specific = specialize(args, template)
    print(f'Running microbenchmarks - this needs an elevated prompt')
    subprocess.run(f'C:\\r\\performance\\artifacts\\bin\\GC.Infrastructure\\Release\\net7.0\\GC.Infrastructure.exe microbenchmarks --configuration {specific}', check=True)

# Omits trailing newline
def run_block(title, args):
# coreruns:
#   {save_name}{output_suffix}_{x}:
#     corerun: {core_root}\clrgcexp_{save_name}.dll
#     path: C:\CoreRuns\EmitEvent_Core_Root\corerun.exe
#     environment_variables:
#       DOTNET_GCName: clrgcexp_{save_name}.dll
#       {{environment_variables}}

# runs:
#   {save_name}{output_suffix}_{x}:
#     corerun: {core_root}\clrgcexp_{save_name}.dll
#     environment_variables:
#       DOTNET_GCName: clrgcexp_{save_name}.dll
#       {{environment_variables}}
    indent1 = '  '
    indent2 = indent1 + indent1
    indent3 = indent2 + indent1
    run_lines = [f'{title}:']
    for save_name, output_suffix in args.save_names:
        output_suffix_use = "-" + output_suffix if output_suffix else ""

        for iter_num in range(args.iterations):
            run_lines.append(f'{indent1}{save_name}{output_suffix_use}_{iter_num}:')
            run_lines.append(f'{indent2}corerun: {args.core_root}\clrgcexp_{save_name}.dll')
            #if title == 'coreruns': # using the name like this is a hack
            #    run_lines.append(f'{indent2}path: {args.core_root}\\corerun.exe')
            run_lines.append(f'{indent2}environment_variables:')
            run_lines.append(f'{indent3}DOTNET_GCName: clrgcexp_{save_name}.dll')
            if output_suffix and output_suffix in asp_envs.configs:
                print(asp_envs.configs[output_suffix])
                for k, v in asp_envs.configs[output_suffix].items():
                    run_lines.append(f'{indent3}{k}: {v}')

    return '\n'.join(run_lines)

def run_asp(args):
    benchmarks = args.asp_benchmarks_use = args.asp_benchmarks
    if not os.path.exists(benchmarks):
        benchmarks = str(gcbuild_path.joinpath(benchmarks))

    if args.asp_include or args.asp_exclude:
        new_benchmarks = benchmarks + '.specific'

        with open(benchmarks, 'r') as r, open(new_benchmarks, 'w') as w:
            first = True
            for line in r:
                line = line.strip()
                if first:
                    key = 'Legend,Base CommandLine'
                    if line != key:
                        raise Exception(f"{benchmarks} ({line}) does not start with '{key}'")
                    first = False
                    w.write(line)
                    w.write('\n')
                else:
                    benchmark = line.split(',')[0]

                    if (
                        (not args.asp_include) or any(re.match(p, benchmark) for p in args.asp_include)
                    ) and (
                        (not args.asp_exclude) or not any(re.match(e, benchmark) for e in args.asp_exclude)
                    ):
                        w.write(line)
                        w.write('\n')

        args.asp_benchmarks_use = new_benchmarks

    template = args.asp_template
    if not os.path.exists(template):
        template = str(gcbuild_path.joinpath(template))

    specific = specialize(args, template,
        {
            'run': run_block('runs', args),
            'benchmark_file': args.asp_benchmarks_use
        })
    print(f'Running aspnetbenchmarks {specific} - this needs an elevated prompt')
    print(f'C:\\r\\performance\\artifacts\\bin\\GC.Infrastructure\\Release\\net7.0\\GC.Infrastructure.exe aspnetbenchmarks --configuration {specific}')
    subprocess.run(f'C:\\r\\performance\\artifacts\\bin\\GC.Infrastructure\\Release\\net7.0\\GC.Infrastructure.exe aspnetbenchmarks --configuration {specific}', check=True)

def run_gcperfsim(args):
    template = str(pathlib.Path(args.gcperfsim_dir).joinpath(args.gcperfsim_file))
    specific = specialize(args, template, { 'run': run_block('coreruns', args) })
    print(f'Running gcperfsim - this needs an elevated prompt')
    exec = f'C:\\r\\performance\\artifacts\\bin\\GC.Infrastructure\\Release\\net7.0\\GC.Infrastructure.exe gcperfsim --configuration {specific}' # --server aspnet-perf-win
    print()
    print(exec)
    print()
    subprocess.run(exec, check=True)

def run_gcperfsim_file(args):
    specific = 'C:\\r\\utils\\gcbuild\\gcperfsim.data.txt'
    exec = f'{args.core_root}\\corerun.exe C:\\r\\performance\\artifacts\\bin\\GCPerfSim\\{args.configuration}\\net7.0\\GCPerfSim.dll -file {specific}'
    print()
    print(exec)
    print()
    subprocess.run(exec)

def run_gcperfsim_cmd(args):
    cmdline = '-tc 36 -tagb 100 -tlgb 0 -lohar 1000 -pohar 0 -sohsr 100-4000 -lohsr 16002400-16004800 -pohsr 100-204800 -sohsi 0 -lohsi 0 -pohsi 0 -sohpi 0 -lohpi 0 -sohfi 0 -lohfi 0 -pohfi 0 -allocType reference -testKind time'
    exec = f'{args.core_root}\\corerun.exe C:\\r\\performance\\artifacts\\bin\\GCPerfSim\\{args.configuration}\\net7.0\\GCPerfSim.dll {cmdline}'
    print()
    print(exec)
    print()
    subprocess.run(exec)

def main():
    args = parse()
    validate(args)
    setup_vals(args)
    if args.build or args.build_only:
        if args.build_and_copy:
            setup_dirs(args)
        build(args)
        if args.build_and_copy:
            copy(args)
    if args.build_tests:
        build_tests(args)
    run(args)

if __name__=="__main__":
    main()
