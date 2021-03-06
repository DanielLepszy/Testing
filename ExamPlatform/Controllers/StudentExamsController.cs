﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Mail;
using ExamPlatform.Data;
using ExamPlatform.Models;
using ExamPlatformDataModel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using ExamPlatform.Logger;
using log4net;
using System.Security.Cryptography;

namespace ExamPlatform.Controllers
{

    public class StudentExamsController : Controller
    {
        ILog logger = SingletonFirst.Instance.GetLogger();
        /// <summary>Decrypts the string.
        /// It's use to descrypt email account password</summary>
        /// <param name="cipherText">The cipher text.</param>
        /// <returns></returns>
        public static string DecryptString(string cipherText)
        {
            string keyString = "E546C8DF278CD5931069B522E695D4F2";
            var fullCipher = Convert.FromBase64String(cipherText);

            var iv = new byte[16];
            var cipher = new byte[fullCipher.Length - iv.Length];

            Buffer.BlockCopy(fullCipher, 0, iv, 0, iv.Length);
            Buffer.BlockCopy(fullCipher, iv.Length, cipher, 0, fullCipher.Length - iv.Length);
            var key = Encoding.UTF8.GetBytes(keyString);

            using (var aesAlg = Aes.Create())
            {
                using (var decryptor = aesAlg.CreateDecryptor(key, iv))
                {
                    string result;
                    using (var msDecrypt = new MemoryStream(cipher))
                    {
                        using (var csDecrypt = new CryptoStream(msDecrypt, decryptor, CryptoStreamMode.Read))
                        {
                            using (var srDecrypt = new StreamReader(csDecrypt))
                            {
                                result = srDecrypt.ReadToEnd();
                            }
                        }
                    }

                    return result;
                }
            }
        }

        /// <summary>Shows the student exams to check. This exams does not contain grade.</summary>
        /// <returns></returns>
        [Route("StudentsExams")]
        [HttpGet]
        public  ActionResult ShowStudentExamsToCheck()
        {
            try
            {
                using (var context = new ExamPlatformDbContext())
                {
              

                    var data = context.Exam
                         .Include(e => e.ExamResult)
                         .Include(e => e.ExamOpenedQuestions)
                         .ThenInclude(e => e.OpenedQuestions)
                         .Include(e => e.ExamClosedQuestions)
                         .Include(a => a.Account)
                         .Where(e => e.ExamResult.Grade == null)
                         .Select(z => z).ToList();

                    List<SingleStudentExamModel> singleModelList = new List<SingleStudentExamModel>();
                    List<StudentExamsModel> modelList = new List<StudentExamsModel>();

                    foreach (var StudentAccount in data)
                    {
                        singleModelList.Clear();
                        foreach (var ExamOpenQuestions in StudentAccount.ExamOpenedQuestions)
                        {
                            singleModelList.Add(
                                new SingleStudentExamModel
                          (
                          ExamOpenQuestions.ExamOpenedQuestionsID,
                          ExamOpenQuestions.OpenedQuestions.Question,
                          ExamOpenQuestions.UserAnswer,
                          ExamOpenQuestions.OpenedQuestions.MaxPoints
                          )
                            );
                        }

                        modelList.Add(
                            new StudentExamsModel
                   (
                        StudentAccount.ExamsID,
                        StudentAccount.Account.AccountsID,
                        StudentAccount.Account.Name,
                        StudentAccount.Account.Surname,
                        new List<SingleStudentExamModel>(singleModelList)
                   ));
                    }
                    return View("StudentExamsList", modelList);
                }
            }

            catch (Exception ex)
            {
                logger.Error("StudentExamsController - ShowStudentExamsToCheck " + ex.Message);
                return View();
            }
        }

        /// <summary>Sets the points for opened questions to each student, next set proper grade depends of gained points.At the end send email with grade to students .
        /// The method get IFormCollection of Opened Questions and gained points for each question</summary>
        /// <param name="AnswerPoint">The answer point.</param>
        /// <returns></returns>
        [HttpPost]
        public ActionResult SetPointsForOpenedQuestionsToStudent(IFormCollection AnswerPoint)
        {
            try
            {
                var StudentExamModel = new Dictionary<string, List<string>>();
                var Results = new Dictionary<int, double>();

                foreach (var x in AnswerPoint)
                {
                    List<String> valueList = new List<string>();
                    if (!x.Equals(AnswerPoint.Last()))
                    {
                        foreach (var value in x.Value)
                        {
                            if(x.Key=="AnswerPoints" && value.Length==0)
                            {
                                TempData["Message"] = "Nie wszystkie odpowiedzi studenta otrzymały odpowiednią punktację";
                                return RedirectToAction("ShowStudentExamsToCheck");
                            }
                            valueList.Add(value);
                        }
                        StudentExamModel.Add(x.Key, valueList);
                    }
                }
                List<int> ExamOpenedQuestionIDList = new List<int>();
                List<double> ResultsList = new List<double>();
                List<int> ExamsID = new List<int>();

                StudentExamModel.ElementAt(1).Value.ForEach(x => ExamOpenedQuestionIDList.Add(Int32.Parse(x)));

                StudentExamModel.ElementAt(4).Value.ForEach(
                    x => ResultsList.Add(double.Parse(x.Replace(".", ","))));

              
                StudentExamModel.ElementAt(0).Value.ForEach(x => ExamsID.Add(Int32.Parse(x)));
                ExamsID = ExamsID.Distinct().ToList();

                for (int i = 0; i < ResultsList.Count(); i++)
                {

                    Results.Add(ExamOpenedQuestionIDList[i], ResultsList[i]);
                }

                using (var context = new ExamPlatformDbContext())
                {
                    List<ExamOpenedQuestions> ExamOQuestionList = new List<ExamOpenedQuestions>();

                    foreach (var x in Results)
                    {
                        var ExamOpenedQuestionModel = (from z in context.ExamOpenedQuestions
                                                       where z.ExamOpenedQuestionsID == x.Key
                                                       select z).Single();

                        ExamOpenedQuestionModel.AnswerPoints = x.Value;
                        ExamOQuestionList.Add(ExamOpenedQuestionModel);

                    }
                    context.ExamOpenedQuestions.UpdateRange(ExamOQuestionList);
                    context.SaveChanges();

                    foreach (var examID in ExamsID)
                    {

                        var score = (from x in context.ExamOpenedQuestions
                                     where x.ExamsID == examID
                                     select x.AnswerPoints).ToList();

                        var result = (from r in context.Results
                                      where r.ExamID == examID
                                      select r).Single();

                        score.ForEach(x => result.Score += x.Value);
                        result.Grade = SetExamGrade(result.Score, result.MaxExamPoints);

                        context.Results.Update(result);
                        context.SaveChanges();
                    }

                   
                }
                return RedirectToAction("ShowStudentExamsToCheck");
            }
            catch (Exception ex)
            {
                logger.Error("StudentExamsController - SetPointsForOpenedQuestionsToStudent " + ex.Message);
                return View();
            }
        }
        
        /// <summary>Sets the exam grade depends on purchased scores.</summary>
        /// <param name="score">The score.</param>
        /// <param name="MaxExamPoints">The maximum exam points.</param>
        /// <returns></returns>
        public double SetExamGrade(double score, double MaxExamPoints)
        {
            try
            {
                double percent = (score * 100) / MaxExamPoints;
                double grade;
                percent = Math.Round(percent);

                if (percent < 50)
                {
                    grade = 2.0;
                }
                else if (percent >= 50 && percent < 70)
                {
                    grade = 3.0;
                }
                else if (percent >= 70 && percent < 85)
                {
                    grade = 4.0;
                }
                else
                {
                    grade = 5.0;
                }
                return grade;
            }
            catch (Exception ex)
            {
                logger.Error("StudentExamsController - SetExamGrade " + ex.Message);
                return 0;
            }
        }
        /// <summary>Sends the email with informations associated with results of exams for each students.</summary>
        /// <param name="ExamIDList">The exam identifier list.</param>
        /// <param name="context">The context.</param>
        public ActionResult SendEmail(IFormCollection StudentData)
    {
        using (var context = new ExamPlatformDbContext())
        {
            try
            {
                String getGrade = StudentData.ElementAt(5).Value;
                String withoutZero = getGrade.Substring(0, 1);
                double grade = Convert.ToDouble(withoutZero);

                String getScore = StudentData.ElementAt(6).Value;
                double score = Convert.ToDouble(getScore);

                String getDate = StudentData.ElementAt(3).Value;
                DateTime date = Convert.ToDateTime(getDate);

                String maxExamScore = StudentData.ElementAt(7).Value;
                double maxScore = Convert.ToDouble(maxExamScore);

                UserEmailInfoModel user = new UserEmailInfoModel
                (
                StudentData.ElementAt(0).Value,
                StudentData.ElementAt(1).Value,
                StudentData.ElementAt(2).Value,
                StudentData.ElementAt(4).Value,
                grade,
                date,
                score,
                maxScore
                );

                    JObject messageTemplate = RenderMessageTemplateFromFile();
                    Dictionary<string,string> emailAccount = GetEmailAccountFromDB();
                    var purveyed = sendMessage(emailAccount, user, messageTemplate);

                    var studentResult = context.Exam
                         .Include(e => e.ExamResult)
                         .Include(a => a.Account)
                        .Where(e => 
                        e.DateOfExam.Date == user.ExamDate.Date &&
                        e.DateOfExam.Hour == user.ExamDate.Hour &&
                        e.DateOfExam.Minute == user.ExamDate.Minute)
                        .Select(z => z.ExamResult).ToList();

                    studentResult.First().ifResultSent= purveyed;

                   context.Results.Update(studentResult.First());
                   context.SaveChanges();

                }
                catch (Exception ex)
                {
                    logger.Error("StudentExamsController - SendEmail " + ex.Message);
                }
                return RedirectToAction("ShowAllCheckedExams");
            }
        }
        /// <summary>Renders the message template from template.json file.</summary>
        /// <returns></returns>
        private JObject RenderMessageTemplateFromFile()
    {
            string path = @"C:\Users\Admin\Desktop\Finish\ASP-.NET-Core\ExamPlatform\template.json";
            JObject o1 = JObject.Parse(System.IO.File.ReadAllText(path,Encoding.GetEncoding("Windows-1250")));

            // read JSON directly from a file
            using (StreamReader file = new StreamReader(path, Encoding.GetEncoding("Windows-1250")))
            //StreamReader file = System.IO.File.OpenText(path)) 
            using (JsonTextReader reader = new JsonTextReader(file))
            {
                JObject o2 = (JObject)JToken.ReadFrom(reader);
                return o2;
            }
    }
        /// <summary>Gets the email account from database and return.</summary>
        /// <returns></returns>
        private Dictionary<string, string> GetEmailAccountFromDB()
        {
            Dictionary<string, string> emailAccount = new Dictionary<string, string>();
            try
            {
                using (var context = new ExamPlatformDbContext())
                {
                    
                    var emailSecureData = (from x in context.EmailAccount
                                           where x.EmailAccountID == 1
                                           select new { x.EmailDomain, x.EmailAccountPassword }).Single();
                    emailAccount.Add("domain", emailSecureData.EmailDomain);
                    emailAccount.Add("emailPass", emailSecureData.EmailAccountPassword);

                    return emailAccount;
                }
            }
            catch(Exception ex)
            {
                logger.Error("StudentExamsController - RenderEmailAccount " + ex.Message);
                return emailAccount;
            }

    }
        /// <summary>Sends the email message to the single student with exam results.Mehtod check if email have been sent properly and then return bool.</summary>
        /// <param name="emailAccount">The email account.</param>
        /// <param name="user">The user.</param>
        /// <param name="message">The message.</param>
        /// <returns></returns>
        private bool sendMessage(Dictionary<string,string> emailAccount, UserEmailInfoModel user, JObject message)
        {
            String subjectTemplate = message.GetValue("messageSubject").Value<String>();
            String bodyTemplate = message.GetValue("messageBody").Value<String>();
            String email = emailAccount["domain"];
            String emailPass = DecryptString(emailAccount["emailPass"]);
            String subject = String.Format(subjectTemplate, user.Course);
            String body = String.Format(bodyTemplate, user.Name, user.Surname, user.Grade, user.Course, user.ExamDate, user.Score, user.MaxScore);

            SmtpClient client = new SmtpClient("smtp.gmail.com");
            client.EnableSsl = true;
            client.Port = 587;
            //client.UseDefaultCredentials = false;
            client.Credentials = new NetworkCredential(email, emailPass);

            MailMessage mailMessage = new MailMessage();
            mailMessage.From = new MailAddress(email);
            mailMessage.To.Add(user.Email);
            mailMessage.Body = body;
            mailMessage.Subject = subject;

            try
            {
                client.Send(mailMessage);
                logger.Info("Mail was sent properly");
                return true;
            }catch(SmtpFailedRecipientException ex)
            {
                logger.Error("StudentExamsController - sendMessage " + ex.Message);
                return false;
            }

        }

        /// <summary>Shows all checked exams.</summary>
        /// <returns></returns>
        [HttpGet]
        public IActionResult ShowAllCheckedExams()
        {
            try
            {
                using (var context = new ExamPlatformDbContext())
                {
                    var UserEmail = (from ex in context.Exam
                                     where ex.ExamResult.Grade != null
                                     join results in context.Results on ex.ExamResult.ExamID equals results.ExamID
                                     join account in context.Account on ex.AccountID equals account.AccountsID
                                     select new { account.Name, account.Surname, account.Email, ex.Course.CourseType, results.Grade, ex.DateOfExam, results.Score,results.MaxExamPoints,results.ifResultSent }).ToList();

                    List<UserEmailInfoModel> modelList = new List<UserEmailInfoModel>();

                    UserEmail.ForEach(x =>
                     modelList.Add(new UserEmailInfoModel
                     (
                        x.Name,
                        x.Surname,
                        x.Email,
                        x.CourseType,
                        x.Grade,
                        x.DateOfExam,
                        x.Score,
                        x.MaxExamPoints,
                        x.ifResultSent
                      ))
                    );
                   return View("ExamsResults", modelList);
                }
            }
            catch (Exception ex)
            {
                logger.Error("StudentExamsController - ShowAllCheckedExams " + ex.Message);
                return View();
            }
        }
    }
    
}

