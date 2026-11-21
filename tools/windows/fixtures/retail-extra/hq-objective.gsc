oracleFixture()
{
    level endon("intermission");
    wait 0.05;
    level.RadioSpawnDelay = 1;
    wait 5.95;
    radio = undefined;
    while (!isdefined(radio))
    {
        for (i = 0; i < level.radio.size; i++)
            if (!level.radio[i].hidden)
            {
                radio = level.radio[i];
                break;
            }
        if (!isdefined(radio))
            wait 0.05;
    }
    players = getentarray("player", "classname");
    offset = -20;
    for (i = 0; i < players.size; i++)
        if (isAlive(players[i]) && players[i].pers["team"] == "allies")
        {
            players[i] setOrigin(radio.origin + (offset, 0, 12));
            offset += 40;
        }
    end = getTime() + 11000;
    while (getTime() < end && level.captured_radios["allies"] == 0)
        wait 0.05;
    if (level.captured_radios["allies"] > 0)
        logPrint("oracle hq capture\n");
}
