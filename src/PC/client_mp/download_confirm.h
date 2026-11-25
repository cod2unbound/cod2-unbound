#ifndef DOWNLOAD_CONFIRM_H
#define DOWNLOAD_CONFIRM_H

#include "cod2_feature_config.h"

/* Keep the reconstructed legacy targets and upstream CoD2x behavior. */
#if COD2_X64 && COD2_IS_PATCH_13 && !COD2_CODX
#define COD2_DOWNLOAD_CONFIRMATION 1

void CL_PrepareDownloadConfirmation(void);
int CL_DownloadConfirmationPending(void);
void CL_ResolveDownloadConfirmation(int allow);
void UI_ShowDownloadConfirmation(void);
#else
#define COD2_DOWNLOAD_CONFIRMATION 0
#endif

#endif
