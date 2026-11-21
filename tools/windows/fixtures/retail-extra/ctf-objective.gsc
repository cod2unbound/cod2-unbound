oracleFixture()
{
    level endon("intermission");
    wait 6.0;
    runner = undefined;
    players = getentarray("player", "classname");
    for (i = 0; i < players.size; i++)
        if (isAlive(players[i]) && players[i].pers["team"] == "allies")
        {
            runner = players[i];
            break;
        }
    if (!isdefined(runner))
        return;
    enemy = getent("axis_flag", "targetname");
    home = getent("allied_flag", "targetname");
    score = getTeamScore("allies");
    runner setOrigin(enemy.origin + (0, 0, 8));
    for (attempt = 0; attempt < 30 && !isdefined(runner.flag); attempt++)
        wait 0.05;
    if (!isdefined(runner.flag))
        return;
    logPrint("oracle ctf pickup\n");
    runner setOrigin(home.origin + (0, 0, 8));
    for (attempt = 0; attempt < 30 && getTeamScore("allies") == score; attempt++)
        wait 0.05;
    if (getTeamScore("allies") == score + 1)
        logPrint("oracle ctf capture\n");
}
