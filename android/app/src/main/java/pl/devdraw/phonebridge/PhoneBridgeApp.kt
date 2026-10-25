package pl.devdraw.phonebridge

import android.app.Activity
import android.app.Application
import android.os.Bundle

class PhoneBridgeApp : Application() {
    override fun onCreate() {
        super.onCreate()
        Notifications.createChannels(this)
        registerActivityLifecycleCallbacks(object : ActivityLifecycleCallbacks {
            override fun onActivityStarted(activity: Activity) { startedActivities++ }
            override fun onActivityStopped(activity: Activity) { startedActivities-- }
            override fun onActivityCreated(activity: Activity, savedInstanceState: Bundle?) {}
            override fun onActivityResumed(activity: Activity) {}
            override fun onActivityPaused(activity: Activity) {}
            override fun onActivitySaveInstanceState(activity: Activity, outState: Bundle) {}
            override fun onActivityDestroyed(activity: Activity) {}
        })
    }

    companion object {
        @Volatile
        private var startedActivities = 0

        /** Czy któreś okno aplikacji jest widoczne – wtedy wolno od razu pokazać okno oferty. */
        val isInForeground: Boolean get() = startedActivities > 0
    }
}
