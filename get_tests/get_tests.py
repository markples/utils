# Usage:
# python get_tests.py <path to unmerged test> <paths to test groups>
#
# Example:
# python
#   get_tests\get_tests.py
#   d:/r/runtime/artifacts/log/TestRun.xml
#   d:/r/runtime2/artifacts/log/JIT.Regression.Regression_1.testRun.xml
#   d:/r/runtime2/artifacts/log/JIT.Regression.Regression_2.testRun.xml
#   ...

import argparse
import os
import sys
import xml.etree.ElementTree as ET

def test_count(list_tests):
    return sum(len(x) for x in list_tests.values())

def add_dict_list_item(d, k, v):
    result = d.setdefault(k, [])
    result.append(v)

def add_dict_list_list(d, k, v):
    result = d.setdefault(k, [])
    result.extend(v)

def update_dict_list(d1, d2):
    for k, v in d2.items():
        add_dict_list_list(d1, k, v)

def load(file, drop, canon, zero=False):
    tree = ET.parse(file)
    root = tree.getroot()
    tests = {}
    for test in root.iter('test'):
        attrib = test.attrib

        # Tests are listed a bit differently (.cmd vs .dll, some capitalization).
        # Normalize here.
        name = attrib['name']
        canon_name = name.replace('\\\\','\\')
        if canon_name.endswith('.dll') or canon_name.endswith('.cmd'):
            canon_name = canon_name[:-4]
        canon_name = canon_name.lower()
        if (any(d in canon_name for d in drop)):
            continue

        for c in canon:
            if c in canon_name:
                canon_name = c
                break
        time = 0 if zero else float(attrib['time'])
        add_dict_list_item(tests, canon_name, (name, time))
    print(f'{file} has {len(tests)} entries and {test_count(tests)} tests')
    return tests

def print_diff(tests1, tests2, label):
    only = []
    keys1 = list(tests1.keys())
    keys1.sort()
    for k in keys1:
        if k not in tests2:
            print(f'only in {label}: {k}')
        else:
            v1 = tests1[k]
            v2 = tests2[k]
            if len(v1) > len(v2):
                print(f'more in {label}: {k} ({len(v1)} > {len(v2)})')

def print_time_diff(tests1, tests2, label, time_abs, time_ratio):
    only = []
    keys = list(tests1.keys() & tests2.keys())
    keys.sort()
    for k in keys:
        v1 = sum(p[1] for p in tests1[k])
        v2 = sum(p[1] for p in tests2[k])
        ratio = float('inf') if v2 == 0 else v1/v2
        if v1 - v2 > time_abs or ratio > time_ratio:
            print(f'slower in {label}: {k} ({v1:.2f} > {v2:.2f}) {ratio:.2f}')


configs = \
[
    {
        'drop1':
        [
            'il_conformance',
        ],
        'drop2':
        [
            'runtime_81018',
            'runtime_81019',
            'runtime_81081',
        ],
        'canon':
        [
            'b598031',
            'github_26491',
            'JIT\Regression\CLR-x86-JIT\V2.0-Beta2\b323557',
        ],
    },
    {
        'drop1':
        [
        ],
        'drop2':
        [
        ],
        'canon':
        [
        ],
    },
]

def group_test_by_directory(tests):
    tests_by_dir = {}
    for test_name, value in tests.items():
        test_dir = "\\".join(test_name.split("\\")[:-2])
        if test_dir not in tests_by_dir:
            tests_by_dir[test_dir] = {}
        tests_by_dir[test_dir][test_name] = value
    return tests_by_dir

def print_grouped_by_dir(tests1, tests2):
    testsgroup1 = group_test_by_directory(tests1)
    testsgroup2 = group_test_by_directory(tests2)

    for (directory_name, tests) in testsgroup1.items():
        tests2 = testsgroup2.get(directory_name, {})
        all_distinct_tests = list(tests.keys() | tests2.keys())
        all_distinct_tests.sort()
        found = False
        for test_name in all_distinct_tests:
            if not(test_name in tests and test_name in tests2 and len(tests[test_name]) == len(tests2[test_name])):
                found = True
                break
        if not found:
            continue
        print(f"\nDirectory {directory_name}")
        for test_name in all_distinct_tests:
            short_name = "\\".join(test_name.split("\\")[-2:])
            if test_name in tests:
                if test_name in tests2:
                    if len(tests[test_name]) > len(tests2[test_name]):
                        print(f"\t More in 1: {short_name} {len(tests[test_name])} > {len(tests2[test_name])}")
                    if len(tests[test_name]) < len(tests2[test_name]):
                        print(f"\t More in 2: {short_name} {len(tests2[test_name])} > {len(tests[test_name])}")
                else:
                    print(f"\t Only in 1: {short_name}")
            elif test_name in tests2:
                print(f"\t Only in 2: {short_name}")

def get(args):
    cmd_parser = argparse.ArgumentParser(fromfile_prefix_chars="@")
    cmd_parser.add_argument(
        "-b",
        help="Switch to second config",
        action="store_true",
    )
    cmd_parser.add_argument(
        "-g",
        "--group",
        help="Group output",
        action="store_true",
    )
    cmd_parser.add_argument(
        "-t",
        "--time",
        help="Display times",
        action="store_true",
    )
    cmd_parser.add_argument(
        "--time-abs",
        help="Threshold (bytes) for displaying time difference, needs -s",
        type=int,
        default=3,
    )
    cmd_parser.add_argument(
        "--time-ratio",
        help="Threshold (ratio) for displaying time difference, needs -s",
        type=float,
        default=1.5,
    )
    cmd_parser.add_argument(
        "-z",
        "--zero-first",
        help="View group 2 times (hack - zeroes the group 1 times)",
        action="store_true",
    )

    cmd_parser.add_argument('files', nargs='+')
    cmd_args = cmd_parser.parse_args(args)
    if cmd_args.zero_first:
        cmd_args.time_abs = 0

    config = configs[1 if cmd_args.b else 0]
    drop1 = config['drop1']
    drop2 = config['drop2']
    canon = config['canon']

    tests1 = load(cmd_args.files[0], drop1, canon, zero=cmd_args.zero_first)
    tests2 = {}
    for file in cmd_args.files[1:]:
        tests = load(file, drop2, canon)
        update_dict_list(tests2, tests)
    print(f'group 1 has {len(tests1)} entries and {test_count(tests1)} tests')
    print(f'group 2 has {len(tests2)} entries and {test_count(tests2)} tests')

    # Used to find information about a specific test
    #to_find = '26491'
    #for name in tests1.keys():
    #    if to_find in name:
    #        print(name)
    #for name in tests2.keys():
    #    if to_find in name:
    #        print(name)

    # Print differences
    if cmd_args.group:
        print_grouped_by_dir(tests1, tests2)
    else:
        print_diff(tests1, tests2, '1')
        print_diff(tests2, tests1, '2')

    # Extend comparison/reporting here
    if cmd_args.time:
        print_time_diff(tests1, tests2, '1', cmd_args.time_abs, cmd_args.time_ratio)
        print_time_diff(tests2, tests1, '2', cmd_args.time_abs, cmd_args.time_ratio)
        print(f"Total in 1: {sum(sum(t[1] for t in v) for v in tests1.values()):.2f}")
        print(f"Total in 2: {sum(sum(t[1] for t in v) for v in tests2.values()):.2f}")
    


if __name__ == '__main__':
    get(sys.argv[1:])
