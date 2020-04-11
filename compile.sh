#!/bin/bash

error() {
	if [ -z "$1" ]; then
		echo "Missing error message." 2>&1
		exit 1
	fi

	echo -e "\033[1;33m$1\033[0m" 2>&1
	exit 1
}

for plugin in */; do
	plugin=${plugin%/}
	echo -e "\033[1;33mBuilding plugin $plugin...\033[0m"

	cd "$plugin" || error "Failed to cd into plugin folder '$plugin'"

	# Build
	dotnet build --framework netcoreapp3.1 --configuration Release || error "Compilation of plugin '$plugin' failed."

	# Move plugin to toplevel plugin folder
	mv "bin/Release/netcoreapp3.1/$plugin.dll" ../ || error "Moving the .dll failed."

	# Remove build directory
	rm -r bin/

	cd ..

	echo -e "\033[1;33mFinished building plugin $plugin!\033[0m"
	echo "---------------------------------------------------"
done
