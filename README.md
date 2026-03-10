# EditCompileReload (ECR)
Hot code reload as a library for .NET 3.5+ driven by a file watcher.

## How to use (with compiler plugin)
Add the library and plugin as package references in the `.csproj` file. Make sure not to include the library in your release build.
```xml
<ItemGroup Condition="'$(Configuration)' == 'Debug'">
  <PackageReference Include="EditCompileReload" Version="_"/>
  <PackageReference Include="EditCompileReload.CompilerPlugin" Version="_"/>
</ItemGroup>
```
Then register the file watcher for the project output assembly.
```csharp
#if DEBUG
using EditCompileReload;
#endif
// ...
#if DEBUG
EcrLog.messageCallback = ...
EcrLog.errorCallback = ...
Ecr.RegisterFileWatcher(\* assembly.dll_orig *\); // <--- Note the .dll_orig when using the compiler plugin
#endif
```
Example:
```csharp
#if DEBUG
using EditCompileReload;
#endif
// ...
#if DEBUG
EcrLog.messageCallback = s => Log.Message(s);
EcrLog.errorCallback = s => Log.Error(s);
Ecr.RegisterFileWatcher(@"C:\Users\User\Documents\EditCompileReload\TestAssembly1\bin\Debug\net472\TestAssembly1.dll_orig"); // <--- Note the .dll_orig when using the compiler plugin
#endif
```

### With `PersonalDefinitions.targets`
If you don't want to check in a dependency on ECR into VCS for other developers
you can create a `.gitignore`d targets file with the required `PackageReference`s.

`PersonalDefinitions.targets`:
```xml
<Project>
  <ItemGroup Condition="'$(Configuration)' == 'Debug'">
    <PackageReference Include="EditCompileReload" Version="_"/>
    <PackageReference Include="EditCompileReload.CompilerPlugin" Version="_"/>
  </ItemGroup>

  <PropertyGroup Condition="'$(Configuration)' == 'Debug'">
    <DefineConstants>$(DefineConstants);EDIT_COMPILE_RELOAD</DefineConstants>
  </PropertyGroup>
</Project>
```
In your `.csproj`:
```xml
<Import Project="PersonalDefinitions.targets" Condition="exists('PersonalDefinitions.targets')" />
```
The file watcher:
```csharp
```csharp
#if EDIT_COMPILE_RELOAD
using EditCompileReload;
#endif
// ...
#if EDIT_COMPILE_RELOAD
Ecr.RegisterFileWatcher(\* assembly.dll_orig *\); // <--- Note the .dll_orig when using the compiler plugin
#endif
```

## How to use (library)
It's best to look at an example in the [test suite](https://github.com/Zetrith/EditCompileReload/blob/master/Tests/RunTest.cs).

## Compiler plugin
The compiler plugin runs an MSBuild task, `HotSwapTask`, which, after the `CopyFilesToOutputDirectory` target:
- Instruments the project assembly adding helpers for hotswapping
- Copies the original project assembly to a file with a `.dll_orig` extension

## Supported edits
- Adding, modifying and removing:
    - Types
    - Static and instance methods
    - Static fields
    - Enumerators and lambdas
    - Generic classes and methods (case not supported by Roslyn before .NET 8.0)
- Debugging
  - Debug symbols are correctly mapped to hotswapped code (WIP)

### Caveats
- Functions running on the stack won't get immediately hotswapped
    - The function needs to be called again for new code to run
    - Function calls are effectively boundaries where hotswapped code can start running
- Stack traces for hotswapped code have twice as many frames as normally
- The original assembly isn't updated for reflection
  - You can still access reloaded assemblies to get new members (f.e. for an attribute scan)
- Compiler-generated code is always hotswapped anew
    - If there's some enumerator in progress it won't get hotswapped, only new instances will run new code
- Multithreading with shared mutable data can cause problems
    - Different threads can start executing new code at different times
    - New code working with such data can break invariants
- It's not possible to:
    - Add members to nested types (current limitation, to be supported)
    - Add instance fields (current limitation, to be supported)
    - Add constructors
    - Add, remove or modify attributes of existing members
    - Change the base type of a type
    - Add virtual methods
    - Change the virtuality of a method
    - Rename members
      - Would require IDE support

#### Performance
- Library
  - For assemblies with at most a thousand types, rewriting (on my machine) takes <2 seconds
  - For bigger assemblies (tens of thousands of types), performance might not be acceptable as rewriting takes >10 seconds
- Reloaded code runs about twice as slow as normal code

## Acknowledgments
Thanks to Washi1337 for [AsmResolver](https://github.com/Washi1337/AsmResolver).
