# vsccc

*vsccc* is a windows command line tool to generate a *clangd* compile_commands.json file from a msbuild/VisualStudio project.

```
usage: vsccc [options] [path_to_project]
options:
    --property:N=V     Provide initial property values.
    --verbose          Log extra information while processing.
```

e.g., `vsccc.exe -p:Platform=x64 -p:Configuration=Debug foo.sln`

If `path_to_project` is a directory, it will be searched a project.  It is an error if there are
more than one solution file in the directory, or no solution files and more than one project file.

If `path_to_project` is ommitted, the current directory will be searched for a project.

The `compile_commands.json` file is written into the same directory as the targetted
solution/project file.  If a `compile_commands.json` file already exists, it is overwritten without
warning/error.

Note: not all compile options are supported, and these will be added as needed.  Submit [requests here](../../issues) or [pull requests here](../../pulls).
