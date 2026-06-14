// SPDX-License-Identifier: MIT
//
// hvigor build entry for the Aether Protocol ArkTS HAR module. The harTasks
// plugin drives compilation/packaging of the shared library under DevEco Studio /
// the hvigor CLI. No custom build logic is required.

import { harTasks } from '@ohos/hvigor-ohos-plugin';

export default {
  system: harTasks,
  plugins: [],
};
