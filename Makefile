all:
	rm build -rf
	nuget restore
	msbuild.exe app/*.csproj '/p:Configuration=release;OutputPath=../build'
