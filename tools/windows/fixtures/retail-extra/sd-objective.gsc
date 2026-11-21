oracleBombGround(position, player)
{
    floor = bulletTrace(position + (0, 0, 64), position - (0, 0, 90), false, player);
    return floor["position"] + (0, 0, 1);
}

oracleFixture()
{
    level endon("intermission");
    wait 0.05;
    level.graceperiod = 0;
    wait 5.95;
    level.planttime = 1.0;
    level.defusetime = 1.0;

    attacker = undefined;
    defender = undefined;
    players = getentarray("player", "classname");
    for (i = 0; i < players.size; i++)
    {
        if (!isAlive(players[i]))
            continue;
        if (players[i].pers["team"] == game["attackers"] && !isdefined(attacker))
            attacker = players[i];
        if (players[i].pers["team"] == game["defenders"] && !isdefined(defender))
            defender = players[i];
    }
    if (!isdefined(attacker) || !isdefined(defender))
        return;

    zones = getentarray("bombzone", "targetname");
    if (zones.size == 0)
        return;
    zone = zones[0];
    offsets[0] = (-82, 0, -28);
    offsets[1] = (82, 0, -28);
    offsets[2] = (0, -82, -28);
    offsets[3] = (-55, -55, -28);
    for (attempt = 0; attempt < offsets.size; attempt++)
    {
        attacker setOrigin(oracleBombGround(zone.origin + offsets[attempt], attacker));
        wait 0.35;
        if (attacker isTouching(zone) && attacker isOnGround() && attacker useButtonPressed())
        {
            logPrint("oracle sd ready\n");
            zone notify("trigger", attacker);
            break;
        }
    }
    for (attempt = 0; attempt < 55 && !isdefined(level.bombmodel); attempt++)
        wait 0.05;
    if (!isdefined(level.bombmodel))
    {
        touching = attacker isTouching(zone);
        grounded = attacker isOnGround();
        using = attacker useButtonPressed();
        logPrint("oracle sd prerequisites touch=" + touching + " ground=" + grounded + " use=" + using + "\n");
        return;
    }

    logPrint("oracle sd plant\n");
    defender setOrigin(oracleBombGround(level.bombmodel.origin + (48, 0, 16), defender));
    trigger = getent("bombtrigger", "targetname");
    for (attempt = 0; attempt < 25; attempt++)
    {
        if (defender isOnGround() && defender useButtonPressed())
        {
            trigger notify("trigger", defender);
            break;
        }
        wait 0.05;
    }
    end = getTime() + 3000;
    while (getTime() < end && isdefined(level.bombglow))
        wait 0.05;
    if (!isdefined(level.bombglow))
        logPrint("oracle sd defuse\n");
}
