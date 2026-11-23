#if defined(COD2_X64) && COD2_X64 && defined(COD2_CODX) && COD2_CODX
#include "cod2x_url.h"
#include "join_link_parser.h"
#include <stdio.h>
#include <strings.h>

int Cod2x_ParseURL(const char *url, Cod2xURL *parsed)
{
    JoinLink result;
    if (!url || !parsed || strncasecmp(url, "cod2x://", 8) || !JoinLink_ParsePayload(url + 8, &result))
        return 0;

    memcpy(parsed->address, result.address, sizeof(parsed->address));
    memcpy(parsed->password, result.password, sizeof(parsed->password));
    parsed->hasPassword = result.hasPassword;
    return 1;
}

int Cod2x_URLCommands(const Cod2xURL *parsed, char *commands, size_t capacity)
{
    int length;
    if (!parsed || !commands || !capacity ||
        !memchr(parsed->address, 0, sizeof(parsed->address)) ||
        !memchr(parsed->password, 0, sizeof(parsed->password)) ||
        !SafeAddress(parsed->address) || !SafePassword(parsed->password))
        return 0;
    if (parsed->hasPassword)
        length = snprintf(commands, capacity, "password \"%s\"\nconnect %s\n", parsed->password, parsed->address);
    else
        length = snprintf(commands, capacity, "connect %s\n", parsed->address);
    if (length < 0 || (size_t)length >= capacity) {
        commands[0] = 0;
        return 0;
    }
    return 1;
}
#endif
