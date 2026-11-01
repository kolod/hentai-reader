package hentaireader.app

import androidx.compose.ui.test.hasSetTextAction
import androidx.compose.ui.test.hasText
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performTextInput
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import hentaireader.host.Preferences
import kotlinx.coroutines.runBlocking
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

/**
 * Screen state survives the activity being recreated from its saved state (007 FR-025, T080):
 * the section, and what the user typed. Reader and search positions after the system stops the
 * process are on the manual checklist (they need a source); not a CI gate.
 */
@RunWith(AndroidJUnit4::class)
class RestoreTest {
    @get:Rule
    val rule = createAndroidComposeRule<MainActivity>()

    private val context = InstrumentationRegistry.getInstrumentation().targetContext

    @Before
    fun confirmed() {
        runBlocking { context.services.updatePrefs { it.copy(adultConfirmed = true, startPage = Preferences.START_LIBRARY) } }
        context.services.start()
        rule.activityRule.scenario.recreate()
    }

    @Test
    fun libraryFilterTextSurvivesRecreation() {
        val field = hasSetTextAction()
        rule.waitUntil(15_000) { rule.onAllNodes(field).fetchSemanticsNodes().isNotEmpty() }
        rule.onNode(field).performTextInput("restore-me")
        rule.waitForIdle()

        rule.activityRule.scenario.recreate()

        rule.waitUntil(15_000) { rule.onAllNodes(hasText("restore-me")).fetchSemanticsNodes().isNotEmpty() }
        rule.onNodeWithText("restore-me").assertExists()
    }
}
