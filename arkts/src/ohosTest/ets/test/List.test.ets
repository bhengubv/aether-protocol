/*
 * Hypium test suite aggregator. DevEco Studio / hvigor discovers tests through
 * this entry; it invokes each imported suite function.
 * SPDX-License-Identifier: MIT
 */

import parityTest from './Parity.test';

export default function testsuite(): void {
  parityTest();
}
