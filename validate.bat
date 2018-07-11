@echo off
FOR /F "tokens=* USEBACKQ" %%F IN (`node --version`) DO (
SET version=%%F
)
ECHO %version%

IF %version:~1,1% NEQ 8 (
	IF %version:~1,2% NEQ 10 (
		ECHO Your Function App is currently set to use Node
		REM ECHO .js %version%, but the runtime requires a 8.x or 10.x version (such as 8.11.1 or 10.6.0). 
		REM ECHO For deployed code, please change WEBSITE_NODE_DEFAULT_VERSION in App Settings. On your local machine, you can change node version using 'nvm' (make sure to quit and restart your code editor to pick up the changes).
	)
)

echo We're working with "%version%"
pause