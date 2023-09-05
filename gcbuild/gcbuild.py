import argparse
import distutils
import os
import subprocess
import sys

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

def parse():
    parser = argparse.ArgumentParser(
        prog='gcbuild',
        description='Tool for building and preserving clr gc',
    )

    parser.add_argument('-a', '--all', action='store_true') # build clr+libs instead of clr.native
    parser.add_argument('-c', '--configuration', default='release', choices=['debug', 'checked', 'release'])
    parser.add_argument('-b', '--build', action='store_true')
    parser.add_argument('-t', '--build-tests', action='store_true')

    parser.add_argument('-r', '--run', action='append', choices=['rf', 'micro', 'asp'])
    parser.add_argument('--trace-type', default='gc', choices=['gc', 'verbose', 'cpu', 'threadtime', 'none'])
    parser.add_argument('--testmix-time', default="00:01:00")

    parser.add_argument('runtime_root')
    parser.add_argument('save_root')
    parser.add_argument('save_name')

    args = parser.parse_args()
    return args

def validate(args):
    if not os.path.isdir(args.runtime_root):
        raise Exception(f"{args.runtime_root} does not exist")

    args.save_loc = os.path.join(args.save_root, args.save_name)
    if os.path.exists(args.save_loc) and args.build:
        raise Exception(f"{args.save_loc} already exists")
    if not os.path.exists(args.save_loc) and not args.build:
        raise Exception(f"{args.save_loc} does not exist")

def setup_vals(args):
    args.gc_dir = os.path.join(args.save_loc, "gc")
    args.artifacts_root = f'{args.runtime_root}\\artifacts\\tests\\coreclr\\windows.x64.{args.configuration}'
    args.core_root = f'{args.artifacts_root}\\Tests\\Core_Root'

def setup_dirs(args):
    os.makedirs(args.save_root, exist_ok=True)
    os.makedirs(args.save_loc)
    os.makedirs(args.gc_dir)

# (binary name, disasm it?)
binaries = [('clrgc.dll', True), ('clrgcexp.dll', True), ('coreclr.dll', False)]

def build(args):
    os.chdir(args.runtime_root)
    target = 'clr+libs' if args.all else 'clr.native'
    subprocess.run(f'build.cmd -c {args.configuration} -lc release {target}', check=True)
    subprocess.run(f'src\\tests\\build.cmd generatelayoutonly x64 {args.configuration} /p:LibrariesConfiguration=Release', check=True)

def build_tests(args):
    os.chdir(f'{args.runtime_root}\\src\\tests')
    subprocess.run(f'build.cmd {args.configuration} test GC\\Stress\\Framework\\ReliabilityFramework.csproj', check=True)

def copy(args):
    os.chdir(args.runtime_root)
    print()
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
        with setup_run(args):
            if 'rf' in args.run:
                run_rf(args)
            if 'micro' in args.run:
                run_micro(args)
            if 'asp' in args.run:
                run_asp(args)

def setup_run(args):
    src = f'{args.save_loc}\\clrgcexp.dll'
    gc_name = f'clrgcexp_{args.save_name}.dll'
    dst = f'{args.core_root}\\{gc_name}'
    print(f'Copying GC from {src} to {dst}')
    distutils.file_util.copy_file(src, dst)
    return EnvVars(complus_gcname = gc_name)

def replace(args, file, new_file):
    with open(file, 'r') as r:
        with open(new_file, 'w') as w:
            for line in r:
                line = (
                    line
                    .replace('{testmix_time}', args.testmix_time)
                    .replace('{save_name}', args.save_name)
                    .replace('{core_root}', args.core_root)
                    .replace('{trace_type}', args.trace_type)
                )
                w.write(line)

def run_rf(args):
    os.chdir(f'{args.artifacts_root}\\GC\Stress\Framework\ReliabilityFramework')
    template = 'C:\\r\\utils\\gcbuild\\testmix_gc_ci.config.template'
    specific = 'C:\\r\\utils\\gcbuild\\testmix_gc_ci.config'
    replace(args, template, specific)
    subprocess.run(f'ReliabilityFramework.cmd -coreroot {args.core_root} {specific}')

def run_micro(args):
    template = 'C:\\r\\utils\\gcbuild\\Microbenchmarks.yaml.template'
    specific = 'C:\\r\\utils\\gcbuild\\Microbenchmarks.yaml'
    replace(args, template, specific)
    print(f'Run under elevated prompt:')
    print(f'C:\\r\\performance\\artifacts\\bin\\GC.Infrastructure\\Release\\net7.0\\GC.Infrastructure.exe microbenchmarks --configuration {specific}')
    input("Press Enter when done...")

def run_asp(args):
    template = 'C:\\r\\utils\\gcbuild\\ASPNetBenchmarks.yaml.template'
    specific = 'C:\\r\\utils\\gcbuild\\ASPNetBenchmarks.yaml'
    replace(args, template, specific)
    print(f'Run under elevated prompt:')
    print(f'C:\\r\\performance\\artifacts\\bin\\GC.Infrastructure\\Release\\net7.0\\GC.Infrastructure.exe aspnetbenchmarks --configuration {specific}')
    input("Press Enter when done...")

def main():
    args = parse()
    validate(args)
    setup_vals(args)
    if args.build:
        setup_dirs(args)
        build(args)
        copy(args)
    if args.build_tests:
        build_tests(args)
    run(args)

if __name__=="__main__":
    main()
