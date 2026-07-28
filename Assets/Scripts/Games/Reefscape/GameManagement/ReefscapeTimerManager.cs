using System.Collections;
using MoSimCore.BaseClasses.GameManagement.TimerManagement;
using MoSimCore.Enums;
using UnityEngine;

namespace Games.Reefscape.GameManagement
{
    public class ReefscapeTimerManager : BaseTimerManager
    {
        protected override float MatchDuration => 15f; // 150
        protected override float TeleopStartTime => 10f; // 135
        protected override float EndgameStartTime => 50f; // 20
        
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