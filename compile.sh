#!/bin/bash

error() {
	if [ -z "$1" ]; then
		echo "Missing error message." 2>&1
		exit 1
	fi

	echo -e "\033[1;31m$1\033[0m" 2>&1
	exit 1
}

isGitUpToDate() {
	git remote update > /dev/null 2>&1

	LOCAL=$(git rev-parse @)
	REMOTE=$(git rev-parse origin/master)

	if [ "$LOCAL" = "$REMOTE" ]; then
		# Up to date
		return 0 # Success exit code
	else
		# Not up to date
		return 1 # Error exit code
	fi
}

compileBot() {
	git pull || error "Failed to pull repository."
	sed -i -e '/GitVersionTask/,+3d' TS3AudioBot/TS3AudioBot.csproj # Fix bug in master with GitVersionTask
	dotnet build --framework netcoreapp3.1 --configuration Release TS3AudioBot || error "Compilation of TS3AudioBot failed."
	rsync -a --progress TS3AudioBot/bin/Release/netcoreapp3.1/ ../../ || error "RSync failed."
	cd ..
	echo -e "\033[1;33mFinished building the bot!\033[0m"
	echo "---------------------------------------------------"
}

# Recompile the bot
echo -e "\033[1;33mRecompiling the bot...\033[0m"
if ! [ -d TS3AudioBot ]; then
	git clone --recursive https://github.com/jwiesler/TS3AudioBot.git || error "Failed to clone repository."
	cd TS3AudioBot || error "Failed to cd into the bot repository."
	compileBot
else
	cd TS3AudioBot || error "Failed to cd into the bot repository."

	if ! isGitUpToDate; then
		compileBot
	else
		cd ..
		echo "Nothing to do."
	fi
fi

for plugin in */; do
	plugin=${plugin%/}

	# Skip the bot repository
	if [ "$plugin" == "TS3AudioBot" ]; then
		continue
	fi

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
