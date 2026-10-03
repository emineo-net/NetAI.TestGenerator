cd C:\Users\steph\source\repos\NetAI.TestGenerator\src\Libraries\NetAI.TestGenerator.Tasks

rm -rf **/obj **/bin

dotnet pack -c Debug

dotnet nuget locals all --clear


