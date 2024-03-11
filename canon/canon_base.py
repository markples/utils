# Shared code between the diff and extract tools

from abc import ABC, abstractmethod
import argparse
import collections
import concurrent.futures
from enum import IntEnum, Flag, auto, unique
import fnmatch
import itertools
import json
import os
import re
import threading
import traceback

from canon_util import *

#
# Configuration settings taken from the command line
#

# Note: ConfigBase and get_base_parser (below) process the same set of arguments.
# Specializations need to provide functionality for both.  This is a quick
# factoring and could probably be done more cleanly.
class ConfigBase:
    def __init__(self, cmd_args):
        self.indent_value = ""

        self.kind = cmd_args.kind
        self.jobs = cmd_args.jobs

        self.output_dir = cmd_args.output_dir

        self.opt_only = cmd_args.opt_only
        self.filespecs = list(itertools.chain(*cmd_args.filespecs))
        self.exclude_filespecs = list(itertools.chain(*cmd_args.exclude_filespecs))
        self.funcspecs = [re.compile(s) for s in itertools.chain(*cmd_args.funcspecs)]
        self.exclude_funcspecs = [
            re.compile(s) for s in itertools.chain(*cmd_args.exclude_funcspecs)
        ]
        self.funcnames = ConfigBase.process_arg_funcnames(
            itertools.chain(*cmd_args.funcnames)
        )
        self.exclude_funcnames = set(itertools.chain(*cmd_args.exclude_funcnames))

        self.debug = cmd_args.debug

    # Construct a dir_name -> func_set mapping for the arguments that are in
    # [dir_name:]func_name format.  Function names without a directory name are
    # added to the None dir.
    def process_arg_funcnames(funcnames_iter):
        funcnames = {}
        for funcname in funcnames_iter:
            split = funcname.split(":")
            if len(split) > 2:
                raise ValueError(
                    "--funcname argument ({}) may not contain two colons".format(
                        funcname
                    )
                )
            dir_name = None
            if len(split) == 2:
                dir_name, funcname = split
            funcnames.setdefault(dir_name, set()).add(funcname)

        return funcnames

    def keep_func(self, inner_dir, funcname):
        if self.funcspecs:
            if not any([funcspec.search(funcname) for funcspec in self.funcspecs]):
                return False

        if self.funcnames:
            dir_set = self.funcnames.get(inner_dir, set())
            global_set = self.funcnames.get(None, set())

            if not ((funcname in dir_set) or (funcname in global_set)):
                return False

        if self.exclude_funcspecs:
            if any([funcspec.search(funcname) for funcspec in self.exclude_funcspecs]):
                return False

        if self.exclude_funcnames:
            if funcname in self.exclude_funcnames:
                return False

        return True

    #
    # Debug printing and indentation
    #

    class Indent:
        def __init__(self, config):
            self.config = config

        def __enter__(self):
            self.config.indent_value += ". "
            return self

        def __exit__(self, type, value, traceback):
            self.config.indent_value = self.config.indent_value[:-2]
            return False

    def indent(self, msg=None, *args, always=False):
        if msg:
            self.print(msg, *args, always=always)
        return ConfigBase.Indent(self)

    def print(self, msg, *args, always=False):
        if always or self.debug:
            if args:
                print(self.indent_value + msg.format(*args))
            else:
                print(self.indent_value + "{}".format(msg))


def get_base_parser():
    cmd_parser = argparse.ArgumentParser(fromfile_prefix_chars="@")

    required_group = cmd_parser.add_argument_group(title="required arguments")
    kind_group = required_group.add_mutually_exclusive_group()
    kind_group.add_argument(
        "-l",
        "--llvm",
        help="Canonicalize LLVM files (default)",
        dest="kind",
        action="store_const",
        default="llvm",
        const="llvm",
    )
    kind_group.add_argument(
        "-a",
        "--arm",
        help="Canonicalize ARM assembly files",
        dest="kind",
        action="store_const",
        const="arm",
    )
    kind_group.add_argument(
        "-x",
        "--x64",
        help="Canonicalize X64 assembly files",
        dest="kind",
        action="store_const",
        const="x64",
    )
    kind_group.add_argument(
        "-y",
        "--jitx64",
        help="Canonicalize JIT X64 assembly files",
        dest="kind",
        action="store_const",
        const="jitx64",
    )

    config_group = cmd_parser.add_argument_group(title="configuration arguments")
    config_group.add_argument(
        "-j",
        "--jobs",
        metavar="N",
        help="Allow N jobs at once; defaults to 1",
        type=int,
        default=1,
    )
    config_group.add_argument(
        "-o",
        "--output-dir",
        help="Set output compare directory root",
        default="AsmDiff",
    )

    filter_group = cmd_parser.add_argument_group(
        title="Filtering arguments"
    )
    filter_group.add_argument(
        "--opt-only",
        help="Include only optimized functions (if such filtering is supported for the file type)",
        action="store_true",
        default=False,
    )
    filter_group.add_argument(
        "-F",
        "--filespec",
        metavar="FILESPEC",
        dest="filespecs",
        help="Set filespec to process, uses file globbing, defaults to *.ll (--llvm) or *.s (--arm)",
        nargs="*",
        action="append",
        default=[],
    )
    filter_group.add_argument(
        "-X",
        "--exclude-filespec",
        metavar="FILESPEC",
        dest="exclude_filespecs",
        help="Set filespec to skip, uses file globbing",
        nargs="*",
        action="append",
        default=[],
    )
    filter_group.add_argument(
        "-S",
        "--funcspec",
        metavar="FUNCSPEC",
        dest="funcspecs",
        help="Set funcspec to process, uses re, defaults to all",
        nargs="*",
        action="append",
        default=[],
    )
    filter_group.add_argument(
        "-T",
        "--exclude-funcspec",
        metavar="FUNCSPEC",
        dest="exclude_funcspecs",
        help="Set funcspec to skip, uses re",
        nargs="*",
        action="append",
        default=[],
    )
    filter_group.add_argument(
        "-N",
        "--funcname",
        metavar="[DIR:]FUNCNAME",
        dest="funcnames",
        help="Set function name to process, must be full name, defaults to all, optionally scoped to the name of the directory containing the assembly file",
        nargs="*",
        action="append",
        default=[],
    )
    filter_group.add_argument(
        "-O",
        "--exclude-funcname",
        metavar="FUNCNAME",
        dest="exclude_funcnames",
        help="Set function name to skip, must be full name",
        nargs="*",
        action="append",
        default=[],
    )

    debug_group = cmd_parser.add_argument_group(title="Options for debugging")
    debug_group.add_argument(
        "--debug", help="debug spew", action="store_true", default=False
    )

    return cmd_parser, required_group, config_group, filter_group, debug_group


# When measuring the time of various tool components, these are the categories that
# they can be assigned.
@unique
class TimeKind(IntEnum):
    Walk = (0,)
    Compare = (1,)
    Copy = (2,)
    Filter = (3,)
    Clean = (4,)
    FuncStart = (5,)
    AddFunc = (6,)
    Blank = (7,)
    Append = (8,)
    FuncEnd = (9,)
    EarlyMatch = (10,)
    Canon = (11,)
    CanonMatch = (12,)
    WriteFunc = (13,)
    WriteDiff = (14,)
    FilterDiff = (15,)


# Events to be counted in the tools
@unique
class CounterKind(IntEnum):
    EarlyCount = (0,)
    EarlyMatch = (1,)
    EarlySolo = (2,)
    CanonCount = (3,)
    CanonMatch = (4,)
    CanonSolo = (5,)
    RawDiff = (6,)
    FinalDiff = (7,)
    FuncDiff = (8,)
    FileDiff = (9,)


# Statistics that are kept by the tools.
#
# Note that "strategies" are specific to the diff tool but are still in this shared type.
# If no strategies are counted (such as in the extract tool), the "report" method
# won't mention them.
class Stats:
    def __init__(self, tag):
        self.tag = tag
        self.timers = [Stopwatch(str(k)) for k in TimeKind]
        self.counters = [0] * len(CounterKind)
        self.strategy_counters = {}

    # Add the result of another Stats instance into this one
    def add(self, stat):
        for pair in zip(self.timers, stat.timers):
            pair[0].add(pair[1])
        for i in CounterKind:
            self.counters[i] += stat.counters[i]
        for key, value in stat.strategy_counters.items():
            self.strategy_counters[key] = self.strategy_counters.get(key, 0) + value

    # Increment a counter
    def incr(self, counter, amount=1):
        self.counters[counter] += amount

    # Increment the count of uses of a strategy
    def incr_strategy(self, name):
        self.strategy_counters[name] = self.strategy_counters.get(name, 0) + 1

    # Pretty-print the stats
    def report(self, indent=None):
        mapping = {
            "tag": self.tag,
            "time": round(sum([sw.total() for sw in self.timers]), 3),
            "times": collections.OrderedDict(
                (sw.name, round(sw.total(), 3))
                for sw in self.timers
                if sw.total() >= 0.001
            ),
            "counters": collections.OrderedDict(
                (str(i), self.counters[i]) for i in CounterKind if self.counters[i]
            ),
        }

        if self.strategy_counters:
            mapping["strategy matches"] = collections.OrderedDict(
                sorted(self.strategy_counters.items())
            )

        return json.dumps(mapping, indent=indent)


# Multi-threading support for the tools
#
# Most multi-_threading_ is done via ProcessPoolExecutor because CPython
# doesn't actually execute threads at the same time.  There is minimal
# multi-threading to control those executions.
#
# launcher should do preliminary work and launch jobs with queue_job
class Controller:
    def __init__(self, config, stats, launcher):
        self.config = config
        self.stats = stats
        self.launcher = launcher

    # Helper method that launches the specified launcher and then does
    # some bookkeeping
    def _launcher_method(self):
        self.launcher(self)

        with self.lock:
            self.all_queued = True
            self.check_jobs()

    # Launches all jobs and waits for them to complete
    def go(self):
        self.lock = threading.RLock()
        self.all_launched = threading.Condition(self.lock)
        self.limit_hit = False
        self.worklist = collections.deque()

        # An abstraction failure -- this is a detail of the diff tool.
        # Functions that move between files between base and diff escape
        # the per-file processing.  They are collected in extra_funcs
        # and processed after all files are completed.
        #
        # { compare_subdir -> ( base_funcs, diff_funcs ) }
        # base_funcs and diff_funcs are { func_name -> Function }
        self.extra_funcs = {}

        self.current_job_count = 0
        self.all_queued = False

        jobs = self.config.jobs
        with concurrent.futures.ProcessPoolExecutor(
            max_workers=jobs
        ) if jobs > 1 else FakeExecutor() as executor:
            self.executor = executor
            with self.lock:
                thread = threading.Thread(target=self._launcher_method)
                thread.start()

                # If execution limits have been set, then we'll want to job
                # queueing jobs.  ProcessPoolExecutor's cancellation isn't
                # very aggressive and seems to still launch max_workers jobs
                # after being told to stop.  So instead we queue new jobs on
                # the executor after old jobs complete.  However, this means
                # we can't rely on the 'with' to determine when all jobs are
                # finished because it's possible tha all -launched- jobs have
                # finished but some are still waiting to be launched.
                # Therefore we explicitly wait for all jobs to be launched.
                self.all_launched.wait()

                # Then we still need to wait for all of those launched jobs
                # to complete, with the outer 'with' automatically does that.

    #
    # threading functions
    #

    # callback for after a job completes
    #
    # record information from the job, check limits, and possibly launch more jobs
    def consume_job_future(self, future):
        try:
            result = future.result()
        except:
            traceback.print_exc()
            result = None

        with self.lock:
            self.current_job_count -= 1
            if result:
                compare_subdir, base_extra_funcs, diff_extra_funcs, child_stats = result

                # For the diff tool, *_extra_funcs are ones that didn't have a match
                # in the same named file.  Save them for the end.
                subdir_funcs = self.extra_funcs.setdefault(compare_subdir, ({}, {}))
                subdir_funcs[0].update(base_extra_funcs)
                subdir_funcs[1].update(diff_extra_funcs)

                self.stats.add(child_stats)

            if self.hit_any_limit():
                self.limit_hit = True
                self.worklist.clear()

            self.check_jobs()

    # Determine if the completed jobs have hit any of the limits for this run
    def hit_any_limit(self):
        return (
            (
                self.config.diff_limit
                and (
                    self.stats.counters[CounterKind.FinalDiff] >= self.config.diff_limit
                )
            )
            or (
                self.config.func_limit
                and (
                    self.stats.counters[CounterKind.FuncDiff] >= self.config.func_limit
                )
            )
            or (
                self.config.file_limit
                and (
                    self.stats.counters[CounterKind.FileDiff] >= self.config.file_limit
                )
            )
        )

    # launch more jobs if needed
    def check_jobs(self):
        while (
            self.current_job_count < self.config.jobs
            and len(self.worklist) > 0
            and not self.limit_hit
        ):
            args = self.worklist.popleft()
            future = self.executor.submit(args[0], *args[1:])
            self.current_job_count += 1
            future.add_done_callback(self.consume_job_future)

        if self.limit_hit or (self.all_queued and len(self.worklist) == 0):
            self.all_launched.notify()

    # add a job to the queue and (possibly) launch jobs
    def queue_job(self, *args):
        with self.lock:
            self.worklist.append(args)
            self.check_jobs()

    # end threading section


# does base and/or diff have a file?
class HasFile(Flag):
    NONE = 0
    BASE = auto()
    DIFF = auto()
    BOTH = BASE | DIFF

    # hasfile mapping is dir -> { file -> HasFile }
    def add_file(dir_dict, dir, filename, value):
        file_dict = dir_dict.setdefault(dir, {})
        file_dict[filename] = file_dict.get(filename, HasFile.NONE) | value

    def add_dirwalk(dir_dict, dir, value, filespecs, exclude_filespecs):
        for dirpath, _, filenames in os.walk(dir):
            dirpath = os.path.relpath(dirpath, dir)
            for filename in filenames:
                if any([fnmatch.fnmatch(filename, filespec) for filespec in filespecs]):
                    if not any(
                        [
                            fnmatch.fnmatch(filename, filespec)
                            for filespec in exclude_filespecs
                        ]
                    ):
                        HasFile.add_file(dir_dict, dirpath, filename, value)

    def print(dir_dict):
        for dir, file_dict in sorted(dir_dict.items()):
            print(dir)
            for file, value in sorted(file_dict.items()):
                print("  {}: {}".format(file, value))


#
# When we read a function out of an input file, we do minimal filtering and cleaning
# and store the results in "lines".  This is what will later be viewed.  A further
# canonicalized version is stored in "canon_lines".  The initial diff between versions
# is computed from "canon_lines".
#
Function = collections.namedtuple("Function", ["lines", "canon_lines"])


def new_Function():
    return Function(lines=[], canon_lines=[])


#
# Parsers contain the parameterization so that the tools can process LLVM, x64, and
# ARM64 disassembly.
#
class Parser(ABC):
    # Default filespec used to discovery disassembly files.
    @abstractmethod
    def default_filespec(self):
        pass

    # Determines whether the line represents debug information.  Used to optionally
    # remove debug information.
    @abstractmethod
    def is_debug_info(self, line):
        pass

    # Determines whether the line encodes a reference to a function or other entity.
    # Used to optionally remove references.
    @abstractmethod
    def is_reference(self, line):
        pass

    # Replaces the function table with a much smaller indicator that there was a
    # function table.  Optionally used because the function table is potentially huge.
    @abstractmethod
    def replace_fntable(self, line):
        pass

    # Replaces a line with one that removes distracting information (e.g., encoding bytes).
    @abstractmethod
    def replace_line(self, line):
        pass

    # Produces a canonicalized version of a line that will be better for diffing.
    @abstractmethod
    def canon_line(self, line):
        pass

    # Returns the function name if the line represents the start of a function.
    # Otherwise return None.
    @abstractmethod
    def func_start(self, line):
        pass

    # Returns whether the current line indicates that the entire function should be skipped.
    @abstractmethod
    def func_unopt_from_line(self, line):
        pass

    # Determines whether the line represents the end of a function.
    @abstractmethod
    def func_end(self, line):
        pass

    # Determines whether the line indicates that a previous line already ended the function.
    # Used for representations that don't have an explicit "end function".
    @abstractmethod
    def func_already_ended(self, line):
        pass

    # Returns the line back or None depending on whether the line should be kept for
    # viewing in diffs.
    def filter_line(self, line, include_references, include_debug_info):
        if not include_references and self.is_reference(line):
            return None
        if not include_debug_info and self.is_debug_info(line):
            return None
        return line

    # Returns a simplified version of the line for viewing in diffs.
    def clean_line(self, line):
        if not self.config.include_fntable:
            line = self.replace_fntable(line)
        line = self.replace_line(line)
        if self.config.max_line_length and len(line) > self.config.max_line_length:
            prefix = "LONG LINE: "
            line = prefix + line[: (self.config.max_line_length - len(prefix))] + "\n"
        return line

    # Named tuple used while splitting files
    #
    # @dataclass is new in Python 3.7.  A backport to 3.6 is available on pypi.
    # @dataclass
    class Item:
        # func: Function
        # last_blank: bool = True

        def __init__(self, func_name, func, last_blank=True):
            self.func_name = func_name
            self.func = func
            self.last_blank = last_blank

    # Returns a mapping { function_name -> Function (which is (lines, canon_lines)) }.
    # The ("___" + file_label) func is used for lines not in any function.
    # Only "lines" is filled in by split_file.  canon_lines is blank and left for a
    # future pass.
    #
    # inner_dir is the name of the innermost directory in which the file is contained.
    # It is used for scoped --filename arguments.
    def split_file(self, stats, file, inner_dir, file_label):
        print("  Split {}".format(file_label))

        include_all_blank_lines = self.config.include_all_blank_lines
        include_debug_info = self.config.include_debug_info
        include_outside = not self.config.only_functions
        include_references = self.config.include_references

        funcs = {}
        current = []  # stack (currently only 0-2 elements)

        if include_outside:
            outside_name = "___" + file_label
            outside = new_Function()
            funcs[outside_name] = outside
            current.append(Parser.Item(func_name=outside_name, func=outside))

        outside_is_trash = not include_outside
        in_func = False
        in_trash = outside_is_trash

        with open(file, "r") as read_file:
            for line in read_file:
                if in_trash:
                    filtered_line = line
                else:
                    with stats.timers[TimeKind.Filter]:
                        filtered_line = self.filter_line(
                            line, include_references, include_debug_info
                        )
                        if filtered_line is None:
                            continue
                    with stats.timers[TimeKind.Clean]:
                        filtered_line = self.clean_line(filtered_line)

                with stats.timers[TimeKind.FuncEnd]:
                    if in_func and self.func_already_ended(filtered_line):
                        in_func = False
                        if not in_trash:
                            current.pop()
                        in_trash = outside_is_trash

                if not in_func:
                    with stats.timers[TimeKind.FuncStart]:
                        func_start_name = self.func_start(filtered_line)
                    with stats.timers[TimeKind.AddFunc]:
                        if func_start_name:
                            # print("start {}".format(func_start_name))
                            in_func = True
                            in_trash = not self.config.keep_func(
                                inner_dir, func_start_name
                            )

                            if not in_trash:
                                # do something better here...
                                clean_func_start_name = func_start_name.replace('?', '_').replace(':', '_').replace('<', '_').replace('>', '_')

                                stats.incr(CounterKind.EarlyCount)
                                func = funcs.get(clean_func_start_name)
                                if func:
                                    # The same function name appears multiple
                                    # times in the input. It's not clear what
                                    # to do with this, so just add some spacing.
                                    func.lines.append("\n")
                                    func.lines.append("\n")
                                else:
                                    funcs[clean_func_start_name] = func = new_Function()
                                current.append(Parser.Item(func_name=clean_func_start_name, func=func))

                if not in_trash:
                    with stats.timers[TimeKind.AddFunc]:
                        if in_func and not in_trash and self.config.opt_only and self.func_unopt_from_line(filtered_line):
                            # Need to undo the above AddFunc (not in_trash) code
                            in_trash = True
                            stats.incr(CounterKind.EarlyCount, -1) # hack
                            del funcs[current[-1].func_name]
                            current.pop()

                if not in_trash:
                    # "" (shouldn't happen) or "\n"
                    with stats.timers[TimeKind.Blank]:
                        if include_all_blank_lines or len(filtered_line) > 1:
                            current[-1].last_blank = False
                        else:
                            if current[-1].last_blank:
                                continue
                            current[-1].last_blank = True

                    with stats.timers[TimeKind.Append]:
                        if in_func or include_outside:
                            current[-1].func.lines.append(filtered_line)

                with stats.timers[TimeKind.FuncEnd]:
                    if in_func and self.func_end(filtered_line):
                        in_func = False
                        if not in_trash:
                            current.pop()
                        in_trash = outside_is_trash

        return funcs


class LlvmParser(Parser):
    def default_filespec(self):
        return "*.ll"

    DI_def = re.compile(r"!\d+ = (?:distinct|!DI|!{)", re.ASCII)

    def is_debug_info(self, line):
        # 'match' means at start of line
        return line.startswith("  call void @llvm.dbg.value") or self.DI_def.match(line)

    def is_reference(self, line):
        return line.startswith(r"declare ")

    def replace_fntable(self, line):
        if line.startswith(r"@SbtFnTable = "):
            end = line.find("]") + 1
            line = line[:end] + "...\n"
        return line

    # Match either i64 or i8* for GPR type since this is something we may occasionally change
    # e.g. for rip/rsp/rbp
    sapphire_cc_regs = re.compile(r"%GuestCtx\*(, (i64|i8\*)){17,17}(, <4 x i32>){16,16}")

    def replace_line(self, line):
        line = self.sapphire_cc_regs.sub("SphCCRegs", line)
        return line

    variable = re.compile(r"(%|!)\d+|(%[a-zA-Z.]+(?:[0-9]+[A-Za-z.]+)*)\d*", re.ASCII)
    block_label = re.compile(
        r"^(?:([A-Za-z.]*)[0-9]+|(Label_)[a-f0-9]+): +(; preds.*)$", re.ASCII
    )

    def canon_line(self, line):
        # only one of \1 or \2 will have a value
        line = self.variable.sub(r"\1\2[[N]]", line)
        block_match = self.block_label.match(line)
        if block_match:
            line = (
                block_match.expand(r"\1\2[[N]]:").ljust(50)
                + block_match.group(3)
                + "\n"
            )
        return line

    func_def = re.compile(r"define [^@]*@([^(]+)\(")

    def func_start(self, line):
        func_match = self.func_def.match(line)  # 'match' means at start of line
        return func_match.group(1) if func_match else None

    def func_unopt_from_line(self, line):
        return False

    def func_end(self, line):
        return line[0] == "}"  # ugh, this is faster.  All lines should have \n.
        # return line.startswith("}")

    def func_already_ended(self, line):
        return False


class ArmParser(Parser):
    def default_filespec(self):
        return "*.s"

    def is_debug_info(self, line):
        return "//DEBUG_VALUE: " in line or "\t.loc" in line

    def is_reference(self, line):
        return False

    def replace_fntable(self, line):
        return line

    encoding_comments = re.compile(r" +// encoding: .*")

    def replace_line(self, line):
        # mov   w16, #41944             // encoding: [0x10,0x7b,0x94,0x52]
        line = self.encoding_comments.sub(r"", line)
        return line

    numbered = re.compile(
        r"(\.Lfunc_begin|\.Lfunc_end|\.Lexception|\.Ltmp|\.LBB(?:\d+)_|\.Lcst_begin|\.Lcst_end|\.Lttbase|\.Lttbaseref|\.LJTI|\.LCPI|\.Ldebug_loc|string offset=|GCC_except_table|x|w|%bb\.|%return)\d+",
        re.ASCII,
    )
    line_comments = re.compile(r" +// .*")

    def canon_line(self, line):
        # numbered things
        line = self.numbered.sub(r"\1[[N]]", line)

        # possible future improvement: remove or simplify this kind of line (optionally?)
        #    // fixup A - offset: 0, value: S_SbtGlobalDispatchDll, kind: fixup_aarch64_pcrel_call26
        # line = self.line_comments.sub(r"", line)

        # Old scripts had these - needed?
        #        -e s/[[:punct:]]%PATHFILE%[[:punct:]]/[[file]]/g ^
        # rem  -e s/\/\/[[:space:]]%PATHFILE%/[[file]]/g ^
        # rem sed ':a;s/\([Ss]h\.*\)[^\. ]/\1./;ta;s/[Ss]h/../g'
        # rem  -e s/[[:space:]]\+\/\//"  "\/\//g ^

        return line

    func_def = re.compile(r"// -- Begin function (.*)")

    def func_start(self, line):
        func_match = self.func_def.search(line)  # 'search' means anywhere in line
        return func_match.group(1) if func_match else None

    def func_unopt_from_line(self, line):
        return False

    def func_end(self, line):
        return "// -- End function" in line

    def func_already_ended(self, line):
        return False


class X64Parser(Parser):
    def default_filespec(self):
        return "*.asm"

    def is_debug_info(self, line):
        return False

    def is_reference(self, line):
        return False

    def replace_fntable(self, line):
        return line

    encoding_bytes1 = re.compile(r"(^  [0-9A-F]{16}:)(?: [0-9A-F]{2}){1,6} +")
    encoding_bytes2 = re.compile(r"^ +(?: [0-9A-F]{2}){1,6}$")

    def replace_line(self, line):
        #  0000000140001006: F2 0F 10 0D 0A D3  movsd       xmm1,mmword ptr [__real@3fa999999999999a]
        #                    03 00
        line = self.encoding_bytes1.sub(r"\1  ", line)
        line = self.encoding_bytes2.sub(r"", line)
        return line

    address = re.compile(r"[0-9A-F]{16}|[0-9A-F]{4,}h")
    def canon_line(self, line):
        line = self.address.sub(r"", line)
        # possible future improvement: remove more distracting numbers?

        return line

    func_def = re.compile(r"^([^ ]+):$")

    def func_start(self, line):
        func_match = self.func_def.match(line)
        return func_match.group(1) if func_match else None

    def func_unopt_from_line(self, line):
        return False

    def func_end(self, line):
        return False

    def func_already_ended(self, line):
        return not line or self.func_def.match(line) is not None


class JITX64Parser(Parser):
    def default_filespec(self):
        return "*.dasm"

    def is_debug_info(self, line):
        return False

    def is_reference(self, line):
        return False

    def replace_fntable(self, line):
        return line

    def replace_line(self, line):
        return line

    rbp_offset = re.compile(r"([rsp|rbp])([\+\-])[0-9A-F]+H")
    comment = re.compile("\s*;.*$")
    IG = re.compile(r"G_M\d+_IG\d+")

    def canon_line(self, line):
        # possible future improvement: remove distracting numbers?
        line = self.rbp_offset.sub(r"\1\2[[N]]", line)
        line = self.comment.sub(r"", line)
        line = self.IG.sub(r"G_M[[N]]_IG[[N]]", line)
        return line

    func_def = re.compile(r"^; Assembly listing for method (.*)$")
    invalid_chars = "():<>+"

    def func_start(self, line):
        func_match = self.func_def.match(line)
        return ''.join(['_' if c in self.invalid_chars else c for c in func_match.group(1)]) if func_match else None

    def func_unopt_from_line(self, line):
        return line.startswith("; MinOpts code")

    def func_end(self, line):
        return line.startswith("; Total bytes of code")

    def func_already_ended(self, line):
        return False


def parser_map():
    return {"llvm": LlvmParser, "arm": ArmParser, "x64": X64Parser, "jitx64": JITX64Parser}
