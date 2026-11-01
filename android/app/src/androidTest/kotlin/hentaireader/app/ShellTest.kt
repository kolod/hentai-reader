package hentaireader.app

import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import hentaireader.host.Preferences
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

/**
 * The navigation shell (007 T043, contracts/android-ui.md): the drawer opens with ☰ and closes on
 * an item and on Back; Back on a section's first screen closes the app. Not a CI gate.
 */
@RunWith(AndroidJUnit4::class)
class ShellTest {
    @get:Rule
    val rule = createAndroidComposeRule<MainActivity>()

    private val context = InstrumentationRegistry.getInstrumentation().targetContext
    private fun text(id: Int) = context.getString(id)

    @Before
    fun confirmed() {
        // The gate itself is covered by the manual checklist; here the app starts past it.
        runBlocking { context.services.updatePrefs { it.copy(adultConfirmed = true, startPage = Preferences.START_HISTORY) } }
        context.services.start()
        rule.activityRule.scenario.recreate()
    }

    @Test
    fun drawerOpensWithMenuAndClosesOnItemAndBack() {
        rule.waitUntil(15_000) { rule.onAllNodes(androidx.compose.ui.test.hasContentDescription(text(R.string.Nav_OpenMenu))).fetchSemanticsNodes().isNotEmpty() }
        rule.onNodeWithContentDescription(text(R.string.Nav_OpenMenu)).performClick()
        rule.onNodeWithText(text(R.string.Nav_Settings)).assertIsDisplayed().performClick()
        rule.waitForIdle()
        rule.onNodeWithContentDescription(text(R.string.Nav_OpenMenu)).performClick()
        rule.onNodeWithText(text(R.string.Nav_Library)).assertIsDisplayed()
        rule.runOnUiThread { rule.activity.onBackPressedDispatcher.onBackPressed() }
        rule.waitForIdle()
        assertTrue("Back closes the drawer, the app stays", !rule.activity.isFinishing)
    }

    @Test
    fun backOnASectionRootClosesTheApp() {
        rule.waitUntil(15_000) { rule.onAllNodes(androidx.compose.ui.test.hasContentDescription(text(R.string.Nav_OpenMenu))).fetchSemanticsNodes().isNotEmpty() }
        rule.runOnUiThread { rule.activity.onBackPressedDispatcher.onBackPressed() }
        rule.waitForIdle()
        assertTrue(rule.activity.isFinishing || rule.activity.isDestroyed)
    }
}
