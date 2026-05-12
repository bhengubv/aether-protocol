// SPDX-License-Identifier: MIT

import { UIAbility, AbilityConstant, Want } from '@kit.AbilityKit';
import { hilog } from '@kit.PerformanceAnalysisKit';
import { window } from '@kit.ArkUI';

const TAG: string = 'EntryAbility';
const DOMAIN: number = 0xf005;

/**
 * Entry ability for Aether Teal — NearLink (SLE) mesh transport node.
 *
 * Lifecycle mirrors the BLE node (android/blue) and Wi-Fi Direct node
 * (android/green): initialise the transport service, load the status page,
 * and clean up on destruction.
 */
export default class EntryAbility extends UIAbility {

  onCreate(want: Want, _launchParam: AbilityConstant.LaunchParam): void {
    hilog.info(DOMAIN, TAG, 'Aether Teal onCreate — want: %{public}s', want.action ?? '');
  }

  onDestroy(): void {
    hilog.info(DOMAIN, TAG, 'Aether Teal onDestroy');
  }

  onWindowStageCreate(windowStage: window.WindowStage): void {
    hilog.info(DOMAIN, TAG, 'onWindowStageCreate — loading pages/Index');

    windowStage.loadContent('pages/Index', (err) => {
      if (err.code !== 0) {
        hilog.error(DOMAIN, TAG, 'loadContent failed: %{public}s', JSON.stringify(err));
        return;
      }
      hilog.info(DOMAIN, TAG, 'pages/Index loaded successfully');
    });
  }

  onWindowStageDestroy(): void {
    hilog.info(DOMAIN, TAG, 'onWindowStageDestroy');
  }

  onForeground(): void {
    hilog.info(DOMAIN, TAG, 'onForeground');
  }

  onBackground(): void {
    hilog.info(DOMAIN, TAG, 'onBackground');
  }
}
