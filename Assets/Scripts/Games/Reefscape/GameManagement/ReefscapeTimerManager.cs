using System.Collections;
using MoSimCore.BaseClasses.GameManagement.TimerManagement;
using MoSimCore.Enums;
using UnityEngine;

namespace Games.Reefscape.GameManagement
{
    public class ReefscapeTimerManager : BaseTimerManager
    {
        protected override float MatchDuration => 30f;
        protected override float TeleopStartTime => 20f;
        protected override float EndgameStartTime => 10f;
        
        protected override void StartTeleopTransition()
        {
            StartCoroutine(HandleTeleopTransition());
        }

        private IEnumerator HandleTeleopTransition()
        {
            PauseTimer();
            Timer = TeleopStartTime;
            UpdateTimerText();
            CurrentRobotState = RobotState.Disabled;
            InvokeAutoEnd();

            yield return new WaitForSeconds(3f);
            
            CurrentGameState = GameState.Teleop;
            CurrentRobotState = RobotState.Enabled;
            ResumeTimer();
            InvokeTeleopStart();
            InvokeGameStateChange();
        }
    }
}