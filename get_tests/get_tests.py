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

import os
import sys
import xml.etree.ElementTree as ET

def load(file, drop):
    tree = ET.parse(file)
    root = tree.getroot()
    tests = {}
    for test in root.iter('test'):
        attrib = test.attrib

        # Tests are listed a bit differently (.cmd vs .dll, some capitalization).
        # Normalize here.
        name = attrib['name'].replace('\\\\','\\')
        if name.endswith('.dll') or name.endswith('.cmd'):
            name = name[:-4]
        name = name.lower()
        if not any((d in name for d in drop)):
            if name in tests:
                print(f'!! dup {name} in {file}')
            tests[name] = attrib['time']
    print(f'{file} has {len(tests)} entries')
    return tests

def print_diff(tests1, tests2, label):
    only = []
    #for name in tests1.keys():
    #    if name not in tests2.keys():
    #        only.append(name)
    only = list(tests1.keys() - tests2.keys())
    only.sort()
    for name in only:
        print(f'only in {label}: {name}')

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
        'drop_both':
        [
            'b598031',
            'github_26491',
            'b323557_il',
        ],
    },
    {
        'drop1':
        [
        ],
        'drop2':
        [
        ],
        'drop_both':
        [
        ],
    },
]

config_index = 0

def group_test_by_directory(tests):
    tests_by_dir = {}
    for test_name, value in tests.items():
        test_dir = "\\".join(test_name.split("\\")[:-2])
        if test_dir not in tests_by_dir:
            tests_by_dir[test_dir] = {}
        tests_by_dir[test_dir][test_name] = value
    return tests_by_dir

def print_grouped_by_dir(test1, tests2):
    testsgroup1 = group_test_by_directory(tests1)
    testsgroup2 = group_test_by_directory(tests2)

    for (directory_name, tests) in testsgroup1.items():
        tests2 = testsgroup2.get(directory_name, {})
        tests1_only = tests.keys() - tests2.keys()
        tests2_only = tests2.keys() - tests.keys()
        all_distinct_tests = list(tests1_only | tests2_only)
        all_distinct_tests.sort()
        if 0 < len(all_distinct_tests):
            print(f"\n\nDirectory {directory_name}")
            for test_name in all_distinct_tests:
                short_name = "\\".join(test_name.split("\\")[-2:])
                if test_name in tests1_only:
                    print(f"\t Only in 1: {short_name}")
                else:
                    print(f"\t Only in 2: {short_name}")

def get(files):
    config = configs[config_index]
    drop1 = config['drop1']
    drop2 = config['drop2']
    drop_both = config['drop_both']

    tests1 = load(files[1], drop1 + drop_both)
    tests2 = {}
    for file in files[2:]:
        tests = load(file, drop2 + drop_both)
        old_size = len(tests2)
        add_size = len(tests)
        tests2.update(tests)
        if old_size + add_size != len(tests2):
            print('!! dup across files')
    print(f'len(tests1) = {len(tests1)}')
    print(f'len(tests2) = {len(tests2)}')

    # Used to find information about a specific test
    #to_find = '26491'
    #for name in tests1.keys():
    #    if to_find in name:
    #        print(name)
    #for name in tests2.keys():
    #    if to_find in name:
    #        print(name)

    # Print differences
    #print_diff(tests1, tests2, '1')
    #print_diff(tests2, tests1, '2')

    # Extend comparison/reporting here
    print_grouped_by_dir(tests1, tests2)
    


if __name__ == '__main__':
    get(sys.argv)
