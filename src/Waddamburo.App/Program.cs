var exitCode = Waddamburo.App.Boot.Run(args);
while (exitCode == Waddamburo.App.Boot.RestartExitCode) // Restart: the game boots again in this process
    exitCode = Waddamburo.App.Boot.Run(Waddamburo.App.Boot.RestartArguments);
return exitCode;
